using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using ScomDbExporter.Config;
using ScomDbExporter.Models;

namespace ScomDbExporter.Modules
{
    internal sealed class AlertExporter : IExporterModule
    {
        public string Name => "Alert";
        public bool Enabled => _settings.Enabled;

        private readonly string _connString;
        private readonly AlertModuleToggle _settings;
        private readonly HttpClient _httpClient = new();

        private DateTime _nextRunUtc = DateTime.MinValue;

        // Thread-safe snapshot
        private readonly object _lock = new();
        private List<AlertDto> _changedAlerts = new();

        // Dedup tracking: AlertId -> LastModified timestamp
        private readonly Dictionary<Guid, DateTime> _lastSeen = new();
        // Track when closed alerts were first seen as closed
        private readonly Dictionary<Guid, DateTime> _closedAt = new();

        public IReadOnlyList<AlertDto> CurrentAlerts
        {
            get
            {
                lock (_lock)
                    return _changedAlerts;
            }
        }

        public AlertExporter(string connString, AlertModuleToggle settings)
        {
            _connString = connString;
            _settings = settings ?? new AlertModuleToggle();
        }

        public void Init()
        {
            // Initial load on startup
            RefreshAlerts();
        }

        public void Tick()
        {
            if (DateTime.UtcNow < _nextRunUtc)
                return;

            _nextRunUtc = DateTime.UtcNow.AddSeconds(_settings.PollSeconds);
            RefreshAlerts();
        }

        private void RefreshAlerts()
        {
            const string sql = @"
SELECT
    a.AlertId,
    a.AlertName,
    a.AlertDescription,
    a.Severity,
    a.Priority,
    a.ResolutionState,
    a.Category,
    a.TimeRaised,
    a.TimeAdded,
    a.LastModified,
    a.TimeResolved,
    a.RepeatCount,
    a.Owner,
    a.ResolvedBy,
    a.TicketId,
    a.Context,
    a.CustomField1,
    a.CustomField2,
    a.CustomField3,
    a.CustomField4,
    a.CustomField5,
    a.CustomField6,
    a.CustomField7,
    a.CustomField8,
    a.CustomField9,
    a.CustomField10,
    a.IsMonitorAlert,
    a.ConnectorId,
    bme.DisplayName,
    bme.FullName
FROM dbo.Alert a WITH (NOLOCK)
LEFT JOIN dbo.BaseManagedEntity bme WITH (NOLOCK)
    ON a.BaseManagedEntityId = bme.BaseManagedEntityId
WHERE @IncludeClosed = 1 OR a.ResolutionState <> 255;
";

            var allAlerts = new List<AlertDto>();

            using (var conn = new SqlConnection(_connString))
            using (var cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@IncludeClosed", _settings.IncludeClosedAlerts ? 1 : 0);

                conn.Open();
                using var r = cmd.ExecuteReader();

                while (r.Read())
                {
                    int severity = r.IsDBNull(3) ? 0 : Convert.ToInt32(r.GetValue(3));
                    int resState = r.IsDBNull(5) ? 0 : Convert.ToInt32(r.GetValue(5));

                    allAlerts.Add(new AlertDto
                    {
                        AlertId = r.GetGuid(0),
                        AlertName = GetStringOrEmpty(r, 1),
                        AlertDescription = GetStringOrEmpty(r, 2),
                        Severity = severity,
                        SeverityText = severity switch
                        {
                            0 => "Information",
                            1 => "Warning",
                            2 => "Critical",
                            _ => "Unknown"
                        },
                        Priority = r.IsDBNull(4) ? 0 : Convert.ToInt32(r.GetValue(4)),
                        ResolutionState = resState,
                        ResolutionStateText = resState switch
                        {
                            0 => "New",
                            255 => "Closed",
                            _ => $"Custom_{resState}"
                        },
                        Category = GetStringOrEmpty(r, 6),
                        TimeRaised = r.IsDBNull(7) ? DateTime.MinValue : r.GetDateTime(7),
                        TimeAdded = r.IsDBNull(8) ? DateTime.MinValue : r.GetDateTime(8),
                        LastModified = r.IsDBNull(9) ? DateTime.MinValue : r.GetDateTime(9),
                        TimeResolved = r.IsDBNull(10) ? null : r.GetDateTime(10),
                        RepeatCount = r.IsDBNull(11) ? 0 : Convert.ToInt32(r.GetValue(11)),
                        Owner = GetStringOrEmpty(r, 12),
                        ResolvedBy = GetStringOrEmpty(r, 13),
                        TicketId = GetStringOrEmpty(r, 14),
                        Context = GetStringOrEmpty(r, 15),
                        CustomField1 = GetStringOrEmpty(r, 16),
                        CustomField2 = GetStringOrEmpty(r, 17),
                        CustomField3 = GetStringOrEmpty(r, 18),
                        CustomField4 = GetStringOrEmpty(r, 19),
                        CustomField5 = GetStringOrEmpty(r, 20),
                        CustomField6 = GetStringOrEmpty(r, 21),
                        CustomField7 = GetStringOrEmpty(r, 22),
                        CustomField8 = GetStringOrEmpty(r, 23),
                        CustomField9 = GetStringOrEmpty(r, 24),
                        CustomField10 = GetStringOrEmpty(r, 25),
                        IsMonitorAlert = !r.IsDBNull(26) && r.GetBoolean(26),
                        ConnectorId = r.IsDBNull(27) ? null : r.GetGuid(27),
                        EntityDisplayName = GetStringOrEmpty(r, 28),
                        EntityFullName = GetStringOrEmpty(r, 29)
                    });
                }
            }

            // Apply dedup logic
            var changed = GetChangedAlerts(allAlerts);

            lock (_lock)
                _changedAlerts = changed;

            // Push changed alerts to Alloy
            if (changed.Count > 0 && !string.IsNullOrEmpty(_settings.AlloyEndpoint))
            {
                PushToAlloy(changed);
            }
        }

        private void PushToAlloy(List<AlertDto> alerts)
        {
            foreach (var alert in alerts)
            {
                try
                {
                    var json = SerializeAlert(alert);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var response = _httpClient.PostAsync(_settings.AlloyEndpoint, content).Result;

                    if (!response.IsSuccessStatusCode)
                    {
                        // Log error but continue processing other alerts
                        System.Diagnostics.Debug.WriteLine(
                            $"Failed to push alert {alert.AlertId} to Alloy: {response.StatusCode}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Exception pushing alert {alert.AlertId} to Alloy: {ex.Message}");
                }
            }
        }

        private static string SerializeAlert(AlertDto a)
        {
            var obj = new
            {
                alert_id = a.AlertId.ToString(),
                alert_name = a.AlertName,
                alert_description = a.AlertDescription,
                severity = a.Severity,
                severity_text = a.SeverityText,
                priority = a.Priority,
                resolution_state = a.ResolutionState,
                resolution_state_text = a.ResolutionStateText,
                category = a.Category,
                time_raised = FormatDate(a.TimeRaised),
                time_added = FormatDate(a.TimeAdded),
                last_modified = FormatDate(a.LastModified),
                time_resolved = a.TimeResolved.HasValue ? FormatDate(a.TimeResolved.Value) : null,
                repeat_count = a.RepeatCount,
                owner = a.Owner,
                resolved_by = a.ResolvedBy,
                ticket_id = a.TicketId,
                context = a.Context,
                custom_field_1 = a.CustomField1,
                custom_field_2 = a.CustomField2,
                custom_field_3 = a.CustomField3,
                custom_field_4 = a.CustomField4,
                custom_field_5 = a.CustomField5,
                custom_field_6 = a.CustomField6,
                custom_field_7 = a.CustomField7,
                custom_field_8 = a.CustomField8,
                custom_field_9 = a.CustomField9,
                custom_field_10 = a.CustomField10,
                entity_display_name = a.EntityDisplayName,
                entity_full_name = a.EntityFullName,
                is_monitor_alert = a.IsMonitorAlert,
                connector_id = a.ConnectorId?.ToString()
            };

            return JsonConvert.SerializeObject(obj);
        }

        private static string FormatDate(DateTime dt)
        {
            return dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        }

        private List<AlertDto> GetChangedAlerts(List<AlertDto> current)
        {
            var changed = new List<AlertDto>();
            var currentIds = new HashSet<Guid>();

            foreach (var alert in current)
            {
                currentIds.Add(alert.AlertId);

                // Check if this alert is new or modified
                if (!_lastSeen.TryGetValue(alert.AlertId, out var lastMod)
                    || alert.LastModified > lastMod)
                {
                    changed.Add(alert);
                    _lastSeen[alert.AlertId] = alert.LastModified;

                    // Track when alert becomes closed
                    if (alert.ResolutionState == 255 && !_closedAt.ContainsKey(alert.AlertId))
                    {
                        _closedAt[alert.AlertId] = DateTime.UtcNow;
                    }
                }
            }

            // Clean up closed alerts after retention period
            CleanupClosedAlerts(currentIds);

            return changed;
        }

        private void CleanupClosedAlerts(HashSet<Guid> currentIds)
        {
            var retentionCutoff = DateTime.UtcNow.AddMinutes(-_settings.ClosedAlertRetentionMinutes);
            var toRemove = new List<Guid>();

            foreach (var kvp in _closedAt)
            {
                // Remove from tracking if:
                // 1. Retention period has passed, OR
                // 2. Alert no longer exists in database
                if (kvp.Value < retentionCutoff || !currentIds.Contains(kvp.Key))
                {
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var id in toRemove)
            {
                _lastSeen.Remove(id);
                _closedAt.Remove(id);
            }
        }

        private static string GetStringOrEmpty(SqlDataReader r, int index)
        {
            return r.IsDBNull(index) ? "" : r.GetString(index);
        }
    }
}
