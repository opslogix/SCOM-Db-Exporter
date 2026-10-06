using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using ScomDbExporter.Config;
using ScomDbExporter.Models;

namespace ScomDbExporter.Modules
{
    internal sealed class StateExporter : IExporterModule
    {
        public string Name => "State";
        public bool Enabled => _settings.Enabled;

        private readonly string _connString;
        private readonly StateModuleToggle _settings;
        private readonly GroupMembershipResolver _resolver;
        private readonly ILogger<StateExporter> _log;

        // SQL datetime floor is 1753-01-01; keep the incremental sentinel above it.
        private static readonly DateTime SqlSafeMin = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private DateTime _nextRunUtc = DateTime.MinValue;
        private DateTime _nextFullReconcileUtc = DateTime.MinValue;
        private DateTime _lastSyncTime = SqlSafeMin;
        private Guid _entityStateMonitorId;

        private readonly object _lock = new();
        private Dictionary<Guid, EntityStateDto> _byId = new();

        // Unit monitor state (optional, see MonitorStateConfig). Keyed by (entity, monitor).
        private Dictionary<(Guid Entity, Guid Monitor), MonitorStateDto> _monitorStates = new();
        private Dictionary<Guid, string> _monitorNames = new();
        private DateTime _monitorLastSync = SqlSafeMin;

        private MonitorStateConfig MonitorSettings => _settings.MonitorState;

        public bool MonitorStateEnabled
            => MonitorSettings != null && MonitorSettings.Enabled;

        public IReadOnlyList<MonitorStateDto> CurrentMonitorState
        {
            get
            {
                var filter = _resolver?.GetAllowedBmes(_settings.Groups);

                lock (_lock)
                {
                    var list = new List<MonitorStateDto>(_monitorStates.Count);
                    foreach (var kv in _monitorStates)
                    {
                        if (filter != null && !filter.Contains(kv.Key.Entity))
                            continue;
                        list.Add(kv.Value);
                    }
                    return list;
                }
            }
        }

        public IReadOnlyList<EntityStateDto> CurrentState
        {
            get
            {
                var filter = _resolver?.GetAllowedBmes(_settings.Groups);

                lock (_lock)
                {
                    var list = new List<EntityStateDto>(_byId.Count);
                    foreach (var kv in _byId)
                    {
                        if (filter != null && !filter.Contains(kv.Key))
                            continue;
                        list.Add(kv.Value);
                    }
                    return list;
                }
            }
        }

        public StateExporter(
            string connString,
            StateModuleToggle settings,
            GroupMembershipResolver resolver,
            ILogger<StateExporter> log)
        {
            _connString = connString;
            _settings = settings ?? new StateModuleToggle();
            _resolver = resolver;
            _log = log;
        }

        public void Init()
        {
            _entityStateMonitorId = LoadEntityStateMonitorId();
            FullReconcile();
            _nextFullReconcileUtc = DateTime.UtcNow.AddMinutes(
                Math.Max(1, _settings.FullReconcileMinutes));

            _log.LogInformation(
                "StateExporter init complete: EntityStateMonitorId={MonitorId}, snapshot={Count} entities, fullReconcileEvery={FullReconcileMin}min",
                _entityStateMonitorId, _byId.Count, _settings.FullReconcileMinutes);

            if (MonitorStateEnabled)
            {
                if (GetPatterns().Count == 0)
                {
                    _log.LogWarning(
                        "Monitor state is enabled but Modules:State:MonitorState:NamePatterns is empty; no monitor state is exported");
                }
                else
                {
                    MonitorFullReconcile();
                }
            }
        }

        public void Tick()
        {
            if (DateTime.UtcNow < _nextRunUtc)
                return;

            _nextRunUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, _settings.PollSeconds));

            bool monitors = MonitorStateEnabled && GetPatterns().Count > 0;

            if (DateTime.UtcNow >= _nextFullReconcileUtc)
            {
                FullReconcile();
                if (monitors)
                    MonitorFullReconcile();
                _nextFullReconcileUtc = DateTime.UtcNow.AddMinutes(
                    Math.Max(1, _settings.FullReconcileMinutes));
            }
            else
            {
                IncrementalRefresh();
                if (monitors)
                    MonitorIncrementalRefresh();
            }
        }

        // -------------------------------
        // UNIT MONITOR STATE (optional)
        // -------------------------------

        private List<string> GetPatterns()
        {
            var list = new List<string>();
            var patterns = MonitorSettings?.NamePatterns;
            if (patterns == null)
                return list;

            foreach (var p in patterns)
            {
                if (!string.IsNullOrWhiteSpace(p))
                    list.Add(p.Trim());
            }
            return list;
        }

        /// <summary>
        /// Resolves the unit monitors whose name matches a pattern. Returns null when the
        /// query fails, so the caller keeps the previous snapshot.
        /// </summary>
        private Dictionary<Guid, string> ResolveMonitors()
        {
            var patterns = GetPatterns();
            var sql = new StringBuilder(
                "SELECT MonitorId, MonitorName FROM dbo.Monitor WITH (NOLOCK) WHERE IsUnitMonitor = 1 AND (");
            for (int i = 0; i < patterns.Count; i++)
            {
                if (i > 0)
                    sql.Append(" OR ");
                sql.Append("MonitorName LIKE @p").Append(i);
            }
            sql.Append(");");

            var result = new Dictionary<Guid, string>();
            try
            {
                using var conn = new SqlConnection(_connString);
                using var cmd = new SqlCommand(sql.ToString(), conn);
                for (int i = 0; i < patterns.Count; i++)
                    cmd.Parameters.AddWithValue("@p" + i, patterns[i]);

                conn.Open();
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    result[r.GetGuid(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
            }
            catch (SqlException ex)
            {
                _log.LogError(ex, "Monitor state: resolving monitor names failed — keeping previous monitors");
                return null;
            }

            return result;
        }

        private static string BuildIdList(IEnumerable<Guid> ids)
        {
            // Guids only (no user text), so the list is safe to inline.
            var sb = new StringBuilder();
            foreach (var id in ids)
            {
                if (sb.Length > 0)
                    sb.Append(',');
                sb.Append('\'').Append(id.ToString("D")).Append('\'');
            }
            return sb.ToString();
        }

        private void MonitorFullReconcile()
        {
            var sw = Stopwatch.StartNew();

            var names = ResolveMonitors();
            if (names == null)
                return;

            int max = Math.Max(1, MonitorSettings.MaxSeries);
            var states = new Dictionary<(Guid Entity, Guid Monitor), MonitorStateDto>();
            DateTime maxLastMod = DateTime.MinValue;

            if (names.Count > 0)
            {
                // TOP (max + 1) so that a pattern that is too broad is detected without
                // reading the whole table.
                string sql = @"
SELECT TOP (" + (max + 1) + @")
    s.BaseManagedEntityId,
    s.MonitorId,
    bme.DisplayName,
    bme.FullName,
    s.HealthState,
    s.LastModified
FROM dbo.State s WITH (NOLOCK)
JOIN dbo.BaseManagedEntity bme WITH (NOLOCK)
    ON s.BaseManagedEntityId = bme.BaseManagedEntityId
WHERE s.MonitorId IN (" + BuildIdList(names.Keys) + @")
  AND bme.IsDeleted = 0
  AND s.HealthState > 0;";

                try
                {
                    using var conn = new SqlConnection(_connString);
                    using var cmd = new SqlCommand(sql, conn);
                    conn.Open();
                    using var r = cmd.ExecuteReader();

                    while (r.Read())
                    {
                        var dto = ReadMonitorRow(r, names, out var lastMod);
                        states[(dto.BaseManagedEntityId, dto.MonitorId)] = dto;
                        if (lastMod > maxLastMod)
                            maxLastMod = lastMod;
                    }
                }
                catch (SqlException ex)
                {
                    _log.LogError(ex,
                        "Monitor state full reconcile SQL failed after {ElapsedMs}ms — keeping previous snapshot",
                        sw.ElapsedMilliseconds);
                    return;
                }

                if (states.Count > max)
                {
                    _log.LogWarning(
                        "Monitor state: the {Patterns} pattern(s) select more than MaxSeries={Max} state rows ({Monitors} monitors); " +
                        "no monitor state is exported. Narrow Modules:State:MonitorState:NamePatterns or raise MaxSeries",
                        GetPatterns().Count, max, names.Count);
                    states.Clear();
                    names.Clear();
                }
            }

            int pruned;
            lock (_lock)
            {
                // Rows of deleted monitors, deleted entities and rows that are now
                // HealthState 0 are not in the new snapshot, so they disappear here.
                pruned = 0;
                foreach (var key in _monitorStates.Keys)
                {
                    if (!states.ContainsKey(key))
                        pruned++;
                }

                _monitorStates = states;
                _monitorNames = names;
                if (maxLastMod > _monitorLastSync)
                    _monitorLastSync = maxLastMod;
            }

            _log.LogInformation(
                "Monitor state full reconcile: {Series} series from {Monitors} monitors (pruned {Pruned}) in {ElapsedMs}ms",
                states.Count, names.Count, pruned, sw.ElapsedMilliseconds);
        }

        private void MonitorIncrementalRefresh()
        {
            Dictionary<Guid, string> names;
            lock (_lock)
                names = _monitorNames;

            if (names.Count == 0)
                return;

            // HealthState is not filtered here: a row that dropped to 0 must be removed.
            string sql = @"
SELECT
    s.BaseManagedEntityId,
    s.MonitorId,
    bme.DisplayName,
    bme.FullName,
    s.HealthState,
    s.LastModified
FROM dbo.State s WITH (NOLOCK)
JOIN dbo.BaseManagedEntity bme WITH (NOLOCK)
    ON s.BaseManagedEntityId = bme.BaseManagedEntityId
WHERE s.MonitorId IN (" + BuildIdList(names.Keys) + @")
  AND bme.IsDeleted = 0
  AND s.LastModified > @LastSync;";

            var changes = new List<MonitorStateDto>();
            DateTime maxLastMod = _monitorLastSync;
            var sw = Stopwatch.StartNew();

            try
            {
                using var conn = new SqlConnection(_connString);
                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@LastSync", _monitorLastSync);

                conn.Open();
                using var r = cmd.ExecuteReader();

                while (r.Read())
                {
                    var dto = ReadMonitorRow(r, names, out var lastMod);
                    changes.Add(dto);
                    if (lastMod > maxLastMod)
                        maxLastMod = lastMod;
                }
            }
            catch (SqlException ex)
            {
                _log.LogError(ex,
                    "Monitor state incremental refresh SQL failed after {ElapsedMs}ms",
                    sw.ElapsedMilliseconds);
                return;
            }

            if (changes.Count == 0)
            {
                _log.LogTrace("Monitor state incremental refresh: 0 changes in {ElapsedMs}ms", sw.ElapsedMilliseconds);
                return;
            }

            int max = Math.Max(1, MonitorSettings.MaxSeries);

            lock (_lock)
            {
                foreach (var dto in changes)
                {
                    var key = (dto.BaseManagedEntityId, dto.MonitorId);
                    if (dto.HealthState > 0)
                    {
                        // New series beyond the limit are held back until the next full reconcile.
                        if (_monitorStates.ContainsKey(key) || _monitorStates.Count < max)
                            _monitorStates[key] = dto;
                    }
                    else
                    {
                        _monitorStates.Remove(key);
                    }
                }

                if (maxLastMod > _monitorLastSync)
                    _monitorLastSync = maxLastMod;
            }

            _log.LogDebug(
                "Monitor state incremental refresh: {Count} changes in {ElapsedMs}ms",
                changes.Count, sw.ElapsedMilliseconds);
        }

        private static MonitorStateDto ReadMonitorRow(
            SqlDataReader r, Dictionary<Guid, string> names, out DateTime lastModified)
        {
            var monitorId = r.GetGuid(1);
            lastModified = r.IsDBNull(5) ? DateTime.MinValue : r.GetDateTime(5);

            return new MonitorStateDto
            {
                BaseManagedEntityId = r.GetGuid(0),
                MonitorId = monitorId,
                MonitorName = names.TryGetValue(monitorId, out var n) ? n : "",
                DisplayName = r.IsDBNull(2) ? "" : r.GetString(2),
                FullName = r.IsDBNull(3) ? "" : r.GetString(3),
                HealthState = r.IsDBNull(4) ? 0 : Convert.ToInt32(r.GetValue(4))
            };
        }

        private void FullReconcile()
        {
            const string sql = @"
SELECT
    s.BaseManagedEntityId,
    bme.DisplayName,
    bme.FullName,
    s.HealthState,
    s.LastModified
FROM dbo.State s WITH (NOLOCK)
JOIN dbo.BaseManagedEntity bme WITH (NOLOCK)
    ON s.BaseManagedEntityId = bme.BaseManagedEntityId
WHERE s.MonitorId = @MonitorId
  AND bme.IsDeleted = 0;";

            var newDict = new Dictionary<Guid, EntityStateDto>();
            DateTime maxLastMod = DateTime.MinValue;
            var sw = Stopwatch.StartNew();

            try
            {
                using var conn = new SqlConnection(_connString);
                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@MonitorId", _entityStateMonitorId);

                conn.Open();
                using var r = cmd.ExecuteReader();

                while (r.Read())
                {
                    var dto = ReadRow(r, out var lastMod);
                    newDict[dto.BaseManagedEntityId] = dto;
                    if (lastMod > maxLastMod)
                        maxLastMod = lastMod;
                }
            }
            catch (SqlException ex)
            {
                _log.LogError(ex,
                    "State full reconcile SQL failed after {ElapsedMs}ms — keeping previous snapshot",
                    sw.ElapsedMilliseconds);
                return;
            }

            int pruned;
            lock (_lock)
            {
                pruned = 0;
                foreach (var existingId in _byId.Keys)
                {
                    if (!newDict.ContainsKey(existingId))
                        pruned++;
                }

                _byId = newDict;
                if (maxLastMod > _lastSyncTime)
                    _lastSyncTime = maxLastMod;
            }

            _log.LogInformation(
                "State full reconcile: {Count} entities (pruned {Pruned}) in {ElapsedMs}ms",
                newDict.Count, pruned, sw.ElapsedMilliseconds);
        }

        private void IncrementalRefresh()
        {
            const string sql = @"
SELECT
    s.BaseManagedEntityId,
    bme.DisplayName,
    bme.FullName,
    s.HealthState,
    s.LastModified
FROM dbo.State s WITH (NOLOCK)
JOIN dbo.BaseManagedEntity bme WITH (NOLOCK)
    ON s.BaseManagedEntityId = bme.BaseManagedEntityId
WHERE s.MonitorId = @MonitorId
  AND bme.IsDeleted = 0
  AND s.LastModified > @LastSync;";

            var changes = new List<EntityStateDto>();
            DateTime maxLastMod = _lastSyncTime;
            var sw = Stopwatch.StartNew();

            try
            {
                using var conn = new SqlConnection(_connString);
                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@MonitorId", _entityStateMonitorId);
                cmd.Parameters.AddWithValue("@LastSync", _lastSyncTime);

                conn.Open();
                using var r = cmd.ExecuteReader();

                while (r.Read())
                {
                    var dto = ReadRow(r, out var lastMod);
                    changes.Add(dto);
                    if (lastMod > maxLastMod)
                        maxLastMod = lastMod;
                }
            }
            catch (SqlException ex)
            {
                _log.LogError(ex,
                    "State incremental refresh SQL failed after {ElapsedMs}ms",
                    sw.ElapsedMilliseconds);
                return;
            }

            if (changes.Count == 0)
            {
                _log.LogTrace("State incremental refresh: 0 changes in {ElapsedMs}ms", sw.ElapsedMilliseconds);
                return;
            }

            lock (_lock)
            {
                foreach (var dto in changes)
                    _byId[dto.BaseManagedEntityId] = dto;

                if (maxLastMod > _lastSyncTime)
                    _lastSyncTime = maxLastMod;
            }

            _log.LogDebug(
                "State incremental refresh: {Count} changes in {ElapsedMs}ms",
                changes.Count, sw.ElapsedMilliseconds);
        }

        private static EntityStateDto ReadRow(SqlDataReader r, out DateTime lastModified)
        {
            int hs = r.IsDBNull(3) ? 0 : Convert.ToInt32(r.GetValue(3));
            lastModified = r.IsDBNull(4) ? DateTime.MinValue : r.GetDateTime(4);

            return new EntityStateDto
            {
                BaseManagedEntityId = r.GetGuid(0),
                DisplayName = r.IsDBNull(1) ? "" : r.GetString(1),
                FullName = r.IsDBNull(2) ? "" : r.GetString(2),
                HealthState = hs,
                HealthText = hs switch
                {
                    1 => "Healthy",
                    2 => "Warning",
                    3 => "Critical",
                    _ => "Unknown"
                }
            };
        }

        private Guid LoadEntityStateMonitorId()
        {
            const string sql = @"
SELECT TOP (1) MonitorId
FROM dbo.Monitor
WHERE MonitorName = 'System.Health.EntityState';
";

            using var conn = new SqlConnection(_connString);
            using var cmd = new SqlCommand(sql, conn);
            conn.Open();

            return (Guid)cmd.ExecuteScalar();
        }
    }
}
