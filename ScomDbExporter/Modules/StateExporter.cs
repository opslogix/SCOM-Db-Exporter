using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
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

        private DateTime _nextRunUtc = DateTime.MinValue;
        private DateTime _nextFullReconcileUtc = DateTime.MinValue;
        private DateTime _lastSyncTime = DateTime.MinValue;
        private Guid _entityStateMonitorId;

        private readonly object _lock = new();
        private Dictionary<Guid, EntityStateDto> _byId = new();

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
        }

        public void Tick()
        {
            if (DateTime.UtcNow < _nextRunUtc)
                return;

            _nextRunUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, _settings.PollSeconds));

            if (DateTime.UtcNow >= _nextFullReconcileUtc)
            {
                FullReconcile();
                _nextFullReconcileUtc = DateTime.UtcNow.AddMinutes(
                    Math.Max(1, _settings.FullReconcileMinutes));
            }
            else
            {
                IncrementalRefresh();
            }
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
