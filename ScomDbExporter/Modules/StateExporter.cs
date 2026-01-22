using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using ScomDbExporter.Config;
using ScomDbExporter.Models;

namespace ScomDbExporter.Modules
{
    internal sealed class StateExporter : IExporterModule
    {
        public string Name => "State";
        public bool Enabled => _settings.Enabled;

        private readonly string _connString;
        private readonly ModuleToggle _settings;

        private DateTime _nextRunUtc = DateTime.MinValue;
        private Guid _entityStateMonitorId;

        // Thread-safe snapshot
        private readonly object _lock = new();
        private List<EntityStateDto> _latestState = new();

        public IReadOnlyList<EntityStateDto> CurrentState
        {
            get
            {
                lock (_lock)
                    return _latestState;
            }
        }

        public StateExporter(string connString, ModuleToggle settings)
        {
            _connString = connString;
            _settings = settings ?? new ModuleToggle();
        }

        public void Init()
        {
            _entityStateMonitorId = LoadEntityStateMonitorId();
        }

        public void Tick()
        {
            if (DateTime.UtcNow < _nextRunUtc)
                return;

            _nextRunUtc = DateTime.UtcNow.AddSeconds(_settings.PollSeconds);
            RefreshState();
        }

        private void RefreshState()
        {
            const string sql = @"
SELECT
    bme.DisplayName,
    bme.FullName,
    s.HealthState
FROM dbo.State s WITH (NOLOCK)
JOIN dbo.BaseManagedEntity bme WITH (NOLOCK)
    ON s.BaseManagedEntityId = bme.BaseManagedEntityId
WHERE s.MonitorId = @MonitorId
  AND bme.IsDeleted = 0;
";

            var list = new List<EntityStateDto>();

            using var conn = new SqlConnection(_connString);
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@MonitorId", _entityStateMonitorId);

            conn.Open();
            using var r = cmd.ExecuteReader();

            while (r.Read())
            {
                int hs = r.IsDBNull(2) ? 0 : Convert.ToInt32(r.GetValue(2));

                list.Add(new EntityStateDto
                {
                    DisplayName = r.IsDBNull(0) ? "" : r.GetString(0),
                    FullName = r.IsDBNull(1) ? "" : r.GetString(1),
                    HealthState = hs,
                    HealthText = hs switch
                    {
                        1 => "Healthy",
                        2 => "Warning",
                        3 => "Critical",
                        _ => "Unknown"
                    }
                });
            }

            lock (_lock)
                _latestState = list;
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
