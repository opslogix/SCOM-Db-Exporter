using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Prometheus;
using ScomDbExporter.Config;
using ScomDbExporter.Models;

namespace ScomDbExporter.Modules
{
    internal sealed class PerformanceExporter : IExporterModule
    {
        public string Name => "Performance";
        public bool Enabled => _settings.Enabled;

        private readonly string _connString;
        private readonly ModuleToggle _settings;
        private readonly GroupMembershipResolver _resolver;
        private readonly ILogger<PerformanceExporter> _log;

        private DateTime _nextRunUtc = DateTime.MinValue;
        private DateTime _nextMetadataRefreshUtc = DateTime.MaxValue;
        private DateTime _lastSyncTime = DateTime.UtcNow.AddMinutes(-5);

        // -------------------------------
        // INSTANCE STATE (NO STATICS)
        // -------------------------------

        // Metadata caches — reloaded periodically via RefreshMetadata()
        private readonly Dictionary<Guid, string> _managedTypes = new();
        private readonly Dictionary<Guid, CounterInfo> _counters = new();
        private readonly Dictionary<Guid, EntityInfo> _entities = new();
        private readonly Dictionary<int, PerfSourceInfo> _perfSources = new();

        // Latest sample per PerformanceSourceInternalId
        private readonly Dictionary<int, PerfSample> _latest = new();

        // Mapping indexes — loaded once at startup, never refreshed
        private readonly Dictionary<string, MappingEntry> _mappingIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MappingEntry> _instanceMappingIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MappingEntry> _classMappingIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MappingEntry> _classInstanceMappingIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string ObjPrefix, string CounterKey, MappingEntry Entry)> _wildcardEntries = new();
        private readonly List<(string TypeName, string ObjPrefix, string CounterKey, MappingEntry Entry)> _classWildcardEntries = new();
        private readonly Dictionary<string, MetricDefinition> _metricDefs = new(StringComparer.OrdinalIgnoreCase);

        private static readonly Gauge RawGauge = Metrics.CreateGauge(
            "scom_raw_value",
            "Unmapped SCOM performance values",
            new GaugeConfiguration
            {
                LabelNames = new[] { "object", "counter", "entity", "instance", "instance_name", "entity_class" }
            });

        private static readonly Histogram SqlQueryDuration = Metrics.CreateHistogram(
            "scom_sql_query_duration_seconds",
            "Duration of SQL queries against the SCOM OperationsManager database",
            new HistogramConfiguration
            {
                LabelNames = new[] { "query" },
                Buckets = new[] { 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1.0, 2.5, 5.0, 10.0 }
            });

        public PerformanceExporter(
            string connString,
            ModuleToggle settings,
            GroupMembershipResolver resolver,
            ILogger<PerformanceExporter> log)
        {
            if (string.IsNullOrWhiteSpace(connString))
                throw new ArgumentException("Connection string is null or empty", nameof(connString));

            _connString = connString;
            _settings = settings ?? new ModuleToggle();
            _resolver = resolver;
            _log = log;
        }

        public void Init()
        {
            LoadMappings();
            LoadManagedTypes();
            LoadCounters();
            LoadEntities();
            LoadPerfSources();

            _nextMetadataRefreshUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, _settings.MetadataRefreshMinutes));

            _log.LogInformation(
                "PerformanceExporter init complete: {MappingCount} exact, {InstanceCount} instance-qualified, " +
                "{ClassCount} class-qualified, {ClassInstanceCount} class+instance-qualified, " +
                "{WildcardCount} wildcard, {ClassWildcardCount} class-wildcard mappings, {MetricDefs} metric defs, " +
                "{TypeCount} managed types, {CounterCount} counters, {EntityCount} entities, {SourceCount} perf sources",
                _mappingIndex.Count, _instanceMappingIndex.Count,
                _classMappingIndex.Count, _classInstanceMappingIndex.Count,
                _wildcardEntries.Count, _classWildcardEntries.Count, _metricDefs.Count,
                _managedTypes.Count, _counters.Count, _entities.Count, _perfSources.Count);
        }

        public void Tick()
        {
            var now = DateTime.UtcNow;

            if (now >= _nextMetadataRefreshUtc)
            {
                RefreshMetadata();
                _nextMetadataRefreshUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, _settings.MetadataRefreshMinutes));
            }

            if (now >= _nextRunUtc)
            {
                _nextRunUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, _settings.PollSeconds));
                Poll();
                PublishMetrics();
            }
        }

        // -------------------------------
        // METADATA REFRESH
        // -------------------------------

        private void RefreshMetadata()
        {
            _log.LogInformation("Refreshing metadata caches (managed types, counters, entities, perf sources)");
            var sw = Stopwatch.StartNew();

            _managedTypes.Clear();
            _counters.Clear();
            _entities.Clear();
            _perfSources.Clear();

            LoadManagedTypes();
            LoadCounters();
            LoadEntities();
            LoadPerfSources();

            _log.LogInformation(
                "Metadata refresh complete in {ElapsedMs}ms: {TypeCount} types, {CounterCount} counters, {EntityCount} entities, {SourceCount} perf sources",
                sw.ElapsedMilliseconds, _managedTypes.Count, _counters.Count, _entities.Count, _perfSources.Count);
        }

        // -------------------------------
        // MAPPINGS
        // -------------------------------

        private void LoadMappings()
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mappings");

            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                return;
            }

            var metricLabelSets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                var config = JsonConvert.DeserializeObject<MappingFile>(File.ReadAllText(file));
                if (config?.Mappings == null)
                    continue;

                foreach (var m in config.Mappings)
                {
                    bool hasClass    = !string.IsNullOrEmpty(m.EntityClass);
                    bool hasInstance = !string.IsNullOrEmpty(m.InstanceName);
                    bool isWildcard  = !string.IsNullOrEmpty(m.ObjectName) &&
                                       m.ObjectName.EndsWith("*", StringComparison.Ordinal);

                    if (hasClass && hasInstance)
                    {
                        // Class + instance qualified — most specific dimension combination.
                        // ObjectName wildcards are not supported for this combination.
                        _classInstanceMappingIndex[MakeClassInstanceKey(m.EntityClass, m.ObjectName, m.CounterName, m.InstanceName)] = m;
                    }
                    else if (hasClass && isWildcard)
                    {
                        string prefix = m.ObjectName
                            .Substring(0, m.ObjectName.Length - 1)
                            .ToLowerInvariant();
                        string counterKey = (m.CounterName ?? "").ToLowerInvariant();
                        _classWildcardEntries.Add((m.EntityClass.ToLowerInvariant(), prefix, counterKey, m));
                    }
                    else if (hasClass)
                    {
                        _classMappingIndex[MakeClassKey(m.EntityClass, m.ObjectName, m.CounterName)] = m;
                    }
                    else if (hasInstance)
                    {
                        // Instance-qualified exact entry (unqualified class).
                        // ObjectName wildcards are not supported for instance-qualified entries.
                        _instanceMappingIndex[MakeInstanceKey(m.ObjectName, m.CounterName, m.InstanceName)] = m;
                    }
                    else if (isWildcard)
                    {
                        string prefix = m.ObjectName
                            .Substring(0, m.ObjectName.Length - 1)
                            .ToLowerInvariant();
                        string counterKey = (m.CounterName ?? "").ToLowerInvariant();
                        _wildcardEntries.Add((prefix, counterKey, m));
                    }
                    else
                    {
                        _mappingIndex[MakeKey(m.ObjectName, m.CounterName)] = m;
                    }

                    if (m.Labels == null) continue;

                    if (!metricLabelSets.TryGetValue(m.MetricName, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        metricLabelSets[m.MetricName] = set;
                    }

                    foreach (var label in m.Labels.Keys)
                        set.Add(label);
                }
            }

            // Most specific (longest) prefix wins when several wildcard entries
            // could match the same ObjectName/CounterName. See TryGetMapping.
            _wildcardEntries.Sort((a, b) => b.ObjPrefix.Length.CompareTo(a.ObjPrefix.Length));
            _classWildcardEntries.Sort((a, b) => b.ObjPrefix.Length.CompareTo(a.ObjPrefix.Length));

            foreach (var kv in metricLabelSets)
            {
                var gauge = Metrics.CreateGauge(
                    kv.Key,
                    "Mapped SCOM metric",
                    new GaugeConfiguration { LabelNames = new List<string>(kv.Value).ToArray() });

                _metricDefs[kv.Key] = new MetricDefinition
                {
                    MetricName = kv.Key,
                    LabelNames = new List<string>(kv.Value).ToArray(),
                    Gauge = gauge
                };
            }
        }

        private static string MakeKey(string obj, string counter)
            => (obj ?? "").ToLowerInvariant() + "|" + (counter ?? "").ToLowerInvariant();

        private static string MakeInstanceKey(string obj, string counter, string instanceName)
            => (obj ?? "").ToLowerInvariant() + "|" + (counter ?? "").ToLowerInvariant() + "|" + (instanceName ?? "").ToLowerInvariant();

        private static string MakeClassKey(string typeName, string obj, string counter)
            => (typeName ?? "").ToLowerInvariant() + "|" + (obj ?? "").ToLowerInvariant() + "|" + (counter ?? "").ToLowerInvariant();

        private static string MakeClassInstanceKey(string typeName, string obj, string counter, string instanceName)
            => (typeName ?? "").ToLowerInvariant() + "|" + (obj ?? "").ToLowerInvariant() + "|" + (counter ?? "").ToLowerInvariant() + "|" + (instanceName ?? "").ToLowerInvariant();

        /// <summary>
        /// Resolves a mapping for the given ObjectName/CounterName/InstanceName/TypeName.
        /// Lookup precedence (most specific first):
        ///   1. Class + instance qualified exact  (TypeName + ObjectName + CounterName + InstanceName)
        ///   2. Unqualified instance qualified     (ObjectName + CounterName + InstanceName)
        ///   3. Class qualified exact              (TypeName + ObjectName + CounterName)
        ///   4. Unqualified exact                  (ObjectName + CounterName)
        ///   5. Class qualified wildcard           (TypeName + ObjectName prefix + CounterName)
        ///   6. Unqualified wildcard               (ObjectName prefix + CounterName)
        /// When the match is a wildcard, <paramref name="matchedPrefix"/> holds the
        /// matched prefix so the caller can derive the {object_suffix} token; it is
        /// null for exact matches.
        /// </summary>
        private bool TryGetMapping(string objectName, string counterName, string instanceName, string typeName, out MappingEntry entry, out string matchedPrefix)
        {
            // 1. Class + instance qualified exact.
            if (!string.IsNullOrEmpty(typeName) && !string.IsNullOrEmpty(instanceName))
            {
                if (_classInstanceMappingIndex.TryGetValue(MakeClassInstanceKey(typeName, objectName, counterName, instanceName), out entry))
                {
                    matchedPrefix = null;
                    return true;
                }
            }

            // 2. Unqualified instance qualified exact.
            if (!string.IsNullOrEmpty(instanceName))
            {
                if (_instanceMappingIndex.TryGetValue(MakeInstanceKey(objectName, counterName, instanceName), out entry))
                {
                    matchedPrefix = null;
                    return true;
                }
            }

            // 3. Class qualified exact.
            if (!string.IsNullOrEmpty(typeName))
            {
                if (_classMappingIndex.TryGetValue(MakeClassKey(typeName, objectName, counterName), out entry))
                {
                    matchedPrefix = null;
                    return true;
                }
            }

            // 4. Unqualified exact.
            if (_mappingIndex.TryGetValue(MakeKey(objectName, counterName), out entry))
            {
                matchedPrefix = null;
                return true;
            }

            string counterLower = (counterName ?? "").ToLowerInvariant();
            string obj = objectName ?? "";

            // 5. Class qualified wildcard. Pre-sorted by descending prefix length.
            if (!string.IsNullOrEmpty(typeName))
            {
                string typeNameLower = typeName.ToLowerInvariant();
                foreach (var (tn, prefix, counterKey, candidate) in _classWildcardEntries)
                {
                    if (tn == typeNameLower && counterLower == counterKey
                        && obj.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        entry = candidate;
                        matchedPrefix = prefix;
                        return true;
                    }
                }
            }

            // 6. Unqualified wildcard. Pre-sorted by descending prefix length.
            foreach (var (prefix, counterKey, candidate) in _wildcardEntries)
            {
                if (counterLower == counterKey
                    && obj.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    entry = candidate;
                    matchedPrefix = prefix;
                    return true;
                }
            }

            entry = null;
            matchedPrefix = null;
            return false;
        }

        // -------------------------------
        // METRIC EXPORT
        // -------------------------------

        private void PublishMetrics()
        {
            var filter = _resolver?.GetAllowedBmes(_settings.Groups);
            int skipped = 0;

            foreach (var s in _latest.Values)
            {
                if (s?.Entity == null || s.Counter == null)
                    continue;

                if (filter != null && !filter.Contains(s.Entity.BaseManagedEntityId))
                {
                    skipped++;
                    continue;
                }

                if (TryGetMapping(s.Counter.ObjectName, s.Counter.CounterName, s.InstanceName, s.Entity?.TypeName, out var map, out var matchedPrefix) &&
                    _metricDefs.TryGetValue(map.MetricName, out var def))
                {
                    double value = s.Value * map.ValueMultiplier;
                    var labels = new string[def.LabelNames.Length];

                    // For a wildcard match, {object_suffix} is the portion of the
                    // actual ObjectName beyond the matched prefix (e.g. "D:\SQLData\").
                    string objectSuffix =
                        matchedPrefix != null && s.Counter.ObjectName != null &&
                        s.Counter.ObjectName.Length >= matchedPrefix.Length
                            ? s.Counter.ObjectName.Substring(matchedPrefix.Length)
                            : "";

                    for (int i = 0; i < def.LabelNames.Length; i++)
                    {
                        var label = def.LabelNames[i];
                        labels[i] =
                            map.Labels != null && map.Labels.TryGetValue(label, out var tpl)
                                ? tpl == "{instance}" ? ExtractInstanceName(s.Entity.FullName)
                                : tpl == "{entity}" ? s.Entity.DisplayName
                                : tpl == "{object_suffix}" ? objectSuffix
                                : tpl == "{instance_name}" ? s.InstanceName ?? ""
                                : tpl == "{entity_class}" ? s.Entity?.TypeName ?? ""
                                : tpl
                                : "";
                    }

                    def.Gauge.WithLabels(labels).Set(value);
                }
                else
                {
                    RawGauge.WithLabels(
                        s.Counter.ObjectName ?? "",
                        s.Counter.CounterName ?? "",
                        s.Entity.DisplayName ?? "",
                        ExtractInstanceName(s.Entity.FullName) ?? "",
                        s.InstanceName ?? "",
                        s.Entity?.TypeName ?? "")
                    .Set(s.Value);
                }
            }

            if (filter != null && skipped > 0)
                _log.LogTrace("Group filter skipped {Skipped} samples", skipped);
        }

        private static string ExtractInstanceName(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return "";

            int idx = fullName.LastIndexOf(':');
            if (idx >= 0 && (idx + 1) < fullName.Length)
                return fullName.Substring(idx + 1);

            return fullName;
        }

        // -------------------------------
        // POLLING
        // -------------------------------

        private void Poll()
        {
            // No JOIN: PerformanceSource is cached in _perfSources.
            const string sql = @"
SELECT PerformanceSourceInternalId, SampleValue, TimeSampled
FROM dbo.PerformanceDataAllView WITH (NOLOCK)
WHERE TimeSampled > @LastSync
  AND SampleValue IS NOT NULL;";

            var sw = Stopwatch.StartNew();
            var rawRows = new List<(int SourceId, double Val, DateTime Ts)>();

            try
            {
                using var conn = new SqlConnection(_connString);
                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@LastSync", _lastSyncTime);
                conn.Open();
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    rawRows.Add((r.GetInt32(0), r.GetDouble(1), r.GetDateTime(2)));
            }
            catch (SqlException ex)
            {
                _log.LogError(ex,
                    "Performance poll SQL query failed after {ElapsedMs}ms",
                    sw.ElapsedMilliseconds);
                return;
            }

            SqlQueryDuration.WithLabels("poll").Observe(sw.Elapsed.TotalSeconds);

            if (sw.Elapsed.TotalSeconds > 5.0)
                _log.LogWarning(
                    "Slow SQL query: poll took {ElapsedSeconds:F1}s ({RowCount} rows)",
                    sw.Elapsed.TotalSeconds, rawRows.Count);

            _log.LogDebug(
                "Polled {RowCount} performance rows in {ElapsedMs}ms (lastSync={LastSync:O})",
                rawRows.Count, sw.ElapsedMilliseconds, _lastSyncTime);

            // DataReader is closed — all on-demand SQL is safe here.
            foreach (var (sourceId, val, ts) in rawRows)
            {
                if (!_perfSources.TryGetValue(sourceId, out var src))
                    src = LoadPerfSourceOnDemand(sourceId);

                if (src.EntityId == Guid.Empty)
                    continue;

                if (!_latest.TryGetValue(sourceId, out var existing) || ts > existing.Timestamp)
                {
                    _latest[sourceId] = new PerfSample
                    {
                        Value = val,
                        Timestamp = ts,
                        InstanceName = src.InstanceName,
                        Entity  = _entities.TryGetValue(src.EntityId,  out var e) ? e : LoadEntityOnDemand(src.EntityId),
                        Counter = _counters.TryGetValue(src.CounterId, out var c) ? c : LoadCounterOnDemand(src.CounterId)
                    };
                }

                if (ts > _lastSyncTime)
                    _lastSyncTime = ts;
            }
        }

        // -------------------------------
        // METADATA
        // -------------------------------

        private void LoadManagedTypes()
        {
            const string sql = "SELECT ManagedTypeId, TypeName FROM dbo.ManagedType";
            var sw = Stopwatch.StartNew();

            using var conn = new SqlConnection(_connString);
            using var cmd = new SqlCommand(sql, conn);
            conn.Open();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!r.IsDBNull(0) && !r.IsDBNull(1))
                    _managedTypes[r.GetGuid(0)] = r.GetString(1);
            }

            sw.Stop();
            SqlQueryDuration.WithLabels("load_managed_types").Observe(sw.Elapsed.TotalSeconds);

            if (sw.Elapsed.TotalSeconds > 10.0)
                _log.LogWarning(
                    "Slow SQL query: load_managed_types took {ElapsedSeconds:F1}s ({Count} rows)",
                    sw.Elapsed.TotalSeconds, _managedTypes.Count);
        }

        private void LoadCounters()
        {
            const string sql = "SELECT PerformanceCounterId, CounterName, ObjectName FROM dbo.PerformanceCounter";
            var sw = Stopwatch.StartNew();

            using var conn = new SqlConnection(_connString);
            using var cmd = new SqlCommand(sql, conn);
            conn.Open();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var info = new CounterInfo
                {
                    PerformanceCounterId = r.GetGuid(0),
                    CounterName = r.IsDBNull(1) ? "" : r.GetString(1),
                    ObjectName  = r.IsDBNull(2) ? "" : r.GetString(2)
                };
                info.LookupKey = MakeKey(info.ObjectName, info.CounterName);
                _counters[info.PerformanceCounterId] = info;
            }

            sw.Stop();
            SqlQueryDuration.WithLabels("load_counters").Observe(sw.Elapsed.TotalSeconds);

            if (sw.Elapsed.TotalSeconds > 10.0)
                _log.LogWarning(
                    "Slow SQL query: load_counters took {ElapsedSeconds:F1}s ({CounterCount} rows)",
                    sw.Elapsed.TotalSeconds, _counters.Count);
        }

        private void LoadEntities()
        {
            // BaseManagedTypeId is used to look up TypeName from the _managedTypes cache,
            // avoiding a JOIN on every load.
            const string sql = @"
SELECT BaseManagedEntityId, DisplayName, Path, FullName, BaseManagedTypeId
FROM dbo.BaseManagedEntity
WHERE IsDeleted = 0";

            var sw = Stopwatch.StartNew();

            using var conn = new SqlConnection(_connString);
            using var cmd = new SqlCommand(sql, conn);
            conn.Open();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var typeId = r.IsDBNull(4) ? Guid.Empty : r.GetGuid(4);
                _managedTypes.TryGetValue(typeId, out var typeName);

                _entities[r.GetGuid(0)] = new EntityInfo
                {
                    BaseManagedEntityId = r.GetGuid(0),
                    DisplayName = r.IsDBNull(1) ? "" : r.GetString(1),
                    Path        = r.IsDBNull(2) ? "" : r.GetString(2),
                    FullName    = r.IsDBNull(3) ? "" : r.GetString(3),
                    TypeName    = typeName ?? ""
                };
            }

            sw.Stop();
            SqlQueryDuration.WithLabels("load_entities").Observe(sw.Elapsed.TotalSeconds);

            if (sw.Elapsed.TotalSeconds > 10.0)
                _log.LogWarning(
                    "Slow SQL query: load_entities took {ElapsedSeconds:F1}s ({EntityCount} rows)",
                    sw.Elapsed.TotalSeconds, _entities.Count);
        }

        private void LoadPerfSources()
        {
            const string sql = @"
SELECT PerformanceSourceInternalId, BaseManagedEntityId, PerformanceCounterId, PerfmonInstanceName
FROM dbo.PerformanceSource WITH (NOLOCK)";

            var sw = Stopwatch.StartNew();

            using var conn = new SqlConnection(_connString);
            using var cmd = new SqlCommand(sql, conn);
            conn.Open();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                _perfSources[r.GetInt32(0)] = new PerfSourceInfo
                {
                    EntityId     = r.GetGuid(1),
                    CounterId    = r.GetGuid(2),
                    InstanceName = r.IsDBNull(3) ? "" : r.GetString(3)
                };
            }

            sw.Stop();
            SqlQueryDuration.WithLabels("load_perf_sources").Observe(sw.Elapsed.TotalSeconds);

            if (sw.Elapsed.TotalSeconds > 10.0)
                _log.LogWarning(
                    "Slow SQL query: load_perf_sources took {ElapsedSeconds:F1}s ({Count} rows)",
                    sw.Elapsed.TotalSeconds, _perfSources.Count);
        }

        private PerfSourceInfo LoadPerfSourceOnDemand(int sourceId)
        {
            const string sql = @"
SELECT PerformanceSourceInternalId, BaseManagedEntityId, PerformanceCounterId, PerfmonInstanceName
FROM dbo.PerformanceSource
WHERE PerformanceSourceInternalId = @id";

            var sw = Stopwatch.StartNew();

            using var conn = new SqlConnection(_connString);
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", sourceId);
            conn.Open();
            using var r = cmd.ExecuteReader();

            PerfSourceInfo result;
            if (r.Read())
            {
                result = new PerfSourceInfo
                {
                    EntityId     = r.GetGuid(1),
                    CounterId    = r.GetGuid(2),
                    InstanceName = r.IsDBNull(3) ? "" : r.GetString(3)
                };
            }
            else
            {
                result = default;
            }

            _perfSources[sourceId] = result;

            sw.Stop();
            SqlQueryDuration.WithLabels("source_on_demand").Observe(sw.Elapsed.TotalSeconds);

            if (sw.Elapsed.TotalSeconds > 1.0)
                _log.LogWarning(
                    "Slow SQL query: source_on_demand took {ElapsedSeconds:F1}s for id={SourceId}",
                    sw.Elapsed.TotalSeconds, sourceId);

            return result;
        }

        private CounterInfo LoadCounterOnDemand(Guid id)
        {
            const string sql = @"
SELECT PerformanceCounterId, CounterName, ObjectName
FROM dbo.PerformanceCounter
WHERE PerformanceCounterId = @id";

            var sw = Stopwatch.StartNew();

            using var conn = new SqlConnection(_connString);
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", id);
            conn.Open();
            using var r = cmd.ExecuteReader();

            CounterInfo result;
            if (r.Read())
            {
                var info = new CounterInfo
                {
                    PerformanceCounterId = id,
                    CounterName = r.IsDBNull(1) ? "" : r.GetString(1),
                    ObjectName  = r.IsDBNull(2) ? "" : r.GetString(2)
                };
                info.LookupKey = MakeKey(info.ObjectName, info.CounterName);
                result = _counters[id] = info;
            }
            else
            {
                result = _counters[id] = new CounterInfo
                {
                    PerformanceCounterId = id,
                    CounterName = $"Counter_{id}",
                    ObjectName  = "Unknown"
                };
            }

            sw.Stop();
            SqlQueryDuration.WithLabels("counter_on_demand").Observe(sw.Elapsed.TotalSeconds);

            if (sw.Elapsed.TotalSeconds > 1.0)
                _log.LogWarning(
                    "Slow SQL query: counter_on_demand took {ElapsedSeconds:F1}s for id={CounterId}",
                    sw.Elapsed.TotalSeconds, id);

            return result;
        }

        private EntityInfo LoadEntityOnDemand(Guid id)
        {
            const string sql = @"
SELECT BaseManagedEntityId, DisplayName, Path, FullName, BaseManagedTypeId
FROM dbo.BaseManagedEntity
WHERE BaseManagedEntityId = @id AND IsDeleted = 0";

            var sw = Stopwatch.StartNew();

            using var conn = new SqlConnection(_connString);
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", id);
            conn.Open();
            using var r = cmd.ExecuteReader();

            EntityInfo result;
            if (r.Read())
            {
                var typeId = r.IsDBNull(4) ? Guid.Empty : r.GetGuid(4);
                _managedTypes.TryGetValue(typeId, out var typeName);

                result = _entities[id] = new EntityInfo
                {
                    BaseManagedEntityId = id,
                    DisplayName = r.IsDBNull(1) ? "" : r.GetString(1),
                    Path        = r.IsDBNull(2) ? "" : r.GetString(2),
                    FullName    = r.IsDBNull(3) ? "" : r.GetString(3),
                    TypeName    = typeName ?? ""
                };
            }
            else
            {
                result = _entities[id] = new EntityInfo
                {
                    BaseManagedEntityId = id,
                    DisplayName = $"Entity_{id}"
                };
            }

            sw.Stop();
            SqlQueryDuration.WithLabels("entity_on_demand").Observe(sw.Elapsed.TotalSeconds);

            if (sw.Elapsed.TotalSeconds > 1.0)
                _log.LogWarning(
                    "Slow SQL query: entity_on_demand took {ElapsedSeconds:F1}s for id={EntityId}",
                    sw.Elapsed.TotalSeconds, id);

            return result;
        }
    }
}
