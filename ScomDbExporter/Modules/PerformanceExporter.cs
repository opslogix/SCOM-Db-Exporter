using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
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

        private DateTime _nextRunUtc = DateTime.MinValue;
        private DateTime _lastSyncTime = DateTime.UtcNow.AddMinutes(-5);

        // -------------------------------
        // INSTANCE STATE (NO STATICS)
        // -------------------------------

        private readonly Dictionary<Guid, CounterInfo> _counters = new();
        private readonly Dictionary<Guid, EntityInfo> _entities = new();
        private readonly Dictionary<int, PerfSample> _latest = new();
        private readonly Dictionary<string, MappingEntry> _mappingIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MetricDefinition> _metricDefs = new(StringComparer.OrdinalIgnoreCase);

        private static readonly Gauge RawGauge = Metrics.CreateGauge(
            "scom_raw_value",
            "Unmapped SCOM performance values",
            new GaugeConfiguration
            {
                LabelNames = new[] { "object", "counter", "entity", "instance" }
            });

        public PerformanceExporter(string connString, ModuleToggle settings)
        {
            if (string.IsNullOrWhiteSpace(connString))
                throw new ArgumentException("Connection string is null or empty", nameof(connString));

            _connString = connString;
            _settings = settings ?? new ModuleToggle();
        }

        public void Init()
        {
            LoadMappings();
            LoadCounters();
            LoadEntities();
        }

        public void Tick()
        {
            if (DateTime.UtcNow < _nextRunUtc)
                return;

            _nextRunUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, _settings.PollSeconds));

            Poll();
            PublishMetrics();
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
                    string key = MakeKey(m.ObjectName, m.CounterName);
                    _mappingIndex[key] = m;

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

        // -------------------------------
        // METRIC EXPORT
        // -------------------------------

        private void PublishMetrics()
        {
            foreach (var s in _latest.Values)
            {
                if (s?.Entity == null || s.Counter == null)
                    continue;

                if (s.Counter.LookupKey != null &&
                    _mappingIndex.TryGetValue(s.Counter.LookupKey, out var map) &&
                    _metricDefs.TryGetValue(map.MetricName, out var def))
                {
                    double value = s.Value * map.ValueMultiplier;
                    var labels = new string[def.LabelNames.Length];

                    for (int i = 0; i < def.LabelNames.Length; i++)
                    {
                        var label = def.LabelNames[i];
                        labels[i] =
                            map.Labels != null && map.Labels.TryGetValue(label, out var tpl)
                                ? tpl == "{instance}" ? ExtractInstanceName(s.Entity.FullName)
                                : tpl == "{entity}" ? s.Entity.DisplayName
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
                        ExtractInstanceName(s.Entity.FullName) ?? "")
                    .Set(s.Value);
                }
            }
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
            const string sql = @"
SELECT
    p.PerformanceSourceInternalId,
    p.SampleValue,
    p.TimeSampled,
    ps.BaseManagedEntityId,
    ps.PerformanceCounterId
FROM dbo.PerformanceDataAllView p WITH (NOLOCK)
JOIN dbo.PerformanceSource ps WITH (NOLOCK)
    ON p.PerformanceSourceInternalId = ps.PerformanceSourceInternalId
WHERE p.TimeSampled > @LastSync
  AND p.SampleValue IS NOT NULL;";

            using var conn = new SqlConnection(_connString);
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@LastSync", _lastSyncTime);

            conn.Open();
            using var r = cmd.ExecuteReader();

            while (r.Read())
            {
                int sourceId = r.GetInt32(0);
                double val = r.GetDouble(1);
                DateTime ts = r.GetDateTime(2);

                var entityId = r.GetGuid(3);
                var counterId = r.GetGuid(4);

                if (!_latest.TryGetValue(sourceId, out var existing) || ts > existing.Timestamp)
                {
                    _latest[sourceId] = new PerfSample
                    {
                        Value = val,
                        Timestamp = ts,
                        Entity = _entities.TryGetValue(entityId, out var e) ? e : LoadEntityOnDemand(entityId),
                        Counter = _counters.TryGetValue(counterId, out var c) ? c : LoadCounterOnDemand(counterId)
                    };
                }

                if (ts > _lastSyncTime)
                    _lastSyncTime = ts;
            }
        }

        // -------------------------------
        // METADATA
        // -------------------------------

        private void LoadCounters()
        {
            const string sql = "SELECT PerformanceCounterId, CounterName, ObjectName FROM dbo.PerformanceCounter";

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
                    ObjectName = r.IsDBNull(2) ? "" : r.GetString(2)
                };
                info.LookupKey = MakeKey(info.ObjectName, info.CounterName);
                _counters[info.PerformanceCounterId] = info;
            }
        }

        private void LoadEntities()
        {
            const string sql = @"
SELECT BaseManagedEntityId, DisplayName, Path, FullName
FROM dbo.BaseManagedEntity
WHERE IsDeleted = 0";

            using var conn = new SqlConnection(_connString);
            using var cmd = new SqlCommand(sql, conn);
            conn.Open();

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                _entities[r.GetGuid(0)] = new EntityInfo
                {
                    BaseManagedEntityId = r.GetGuid(0),
                    DisplayName = r.IsDBNull(1) ? "" : r.GetString(1),
                    Path = r.IsDBNull(2) ? "" : r.GetString(2),
                    FullName = r.IsDBNull(3) ? "" : r.GetString(3)
                };
            }
        }

        private CounterInfo LoadCounterOnDemand(Guid id)
        {
            const string sql = @"
SELECT PerformanceCounterId, CounterName, ObjectName
FROM dbo.PerformanceCounter
WHERE PerformanceCounterId = @id";

            using var conn = new SqlConnection(_connString);
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", id);
            conn.Open();

            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                var info = new CounterInfo
                {
                    PerformanceCounterId = id,
                    CounterName = r.IsDBNull(1) ? "" : r.GetString(1),
                    ObjectName = r.IsDBNull(2) ? "" : r.GetString(2)
                };
                info.LookupKey = MakeKey(info.ObjectName, info.CounterName);
                return _counters[id] = info;
            }

            return _counters[id] = new CounterInfo
            {
                PerformanceCounterId = id,
                CounterName = $"Counter_{id}",
                ObjectName = "Unknown"
            };
        }

        private EntityInfo LoadEntityOnDemand(Guid id)
        {
            const string sql = @"
SELECT BaseManagedEntityId, DisplayName, Path, FullName
FROM dbo.BaseManagedEntity
WHERE BaseManagedEntityId = @id AND IsDeleted = 0";

            using var conn = new SqlConnection(_connString);
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", id);
            conn.Open();

            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                return _entities[id] = new EntityInfo
                {
                    BaseManagedEntityId = id,
                    DisplayName = r.IsDBNull(1) ? "" : r.GetString(1),
                    Path = r.IsDBNull(2) ? "" : r.GetString(2),
                    FullName = r.IsDBNull(3) ? "" : r.GetString(3)
                };
            }

            return _entities[id] = new EntityInfo
            {
                BaseManagedEntityId = id,
                DisplayName = $"Entity_{id}"
            };
        }
    }
}
