using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging;
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
        private readonly GroupMembershipResolver _resolver;
        private readonly ILogger<AlertExporter> _log;
        private readonly HttpClient _httpClient = new();

        // SQL datetime floor is 1753-01-01; use a safely-typed sentinel so the
        // bootstrap query fetches all currently-open alerts.
        private static readonly DateTime SqlSafeMin = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private DateTime _nextRunUtc = DateTime.MinValue;
        private DateTime _lastSyncTime = SqlSafeMin;

        private readonly object _lock = new();
        private List<AlertDto> _changedAlerts = new();

        public IReadOnlyList<AlertDto> CurrentAlerts
        {
            get
            {
                lock (_lock)
                    return _changedAlerts;
            }
        }

        public AlertExporter(
            string connString,
            AlertModuleToggle settings,
            GroupMembershipResolver resolver,
            ILogger<AlertExporter> log)
        {
            _connString = connString;
            _settings = settings ?? new AlertModuleToggle();
            _resolver = resolver;
            _log = log;
        }

        public void Init()
        {
            _log.LogDebug("Initial alert load on startup");
            // First poll is a full load (LastSync = MinValue), then we stay incremental.
            RefreshAlerts();
        }

        public void Tick()
        {
            if (DateTime.UtcNow < _nextRunUtc)
                return;

            _nextRunUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, _settings.PollSeconds));
            RefreshAlerts();
        }

        private void RefreshAlerts()
        {
            // Incremental on LastModified; first call after startup has _lastSyncTime=MinValue
            // and therefore fetches all currently-open alerts (the bootstrap snapshot).
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
    a.BaseManagedEntityId,
    bme.DisplayName,
    bme.FullName
FROM dbo.Alert a WITH (NOLOCK)
LEFT JOIN dbo.BaseManagedEntity bme WITH (NOLOCK)
    ON a.BaseManagedEntityId = bme.BaseManagedEntityId
WHERE a.LastModified > @LastSync
  AND (@IncludeClosed = 1 OR a.ResolutionState <> 255);
";

            var changes = new List<AlertDto>();
            DateTime maxLastMod = _lastSyncTime;
            var sw = Stopwatch.StartNew();

            try
            {
                using var conn = new SqlConnection(_connString);
                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@LastSync", _lastSyncTime);
                cmd.Parameters.AddWithValue("@IncludeClosed", _settings.IncludeClosedAlerts ? 1 : 0);

                conn.Open();
                using var r = cmd.ExecuteReader();

                while (r.Read())
                {
                    var dto = ReadRow(r);
                    changes.Add(dto);
                    if (dto.LastModified > maxLastMod)
                        maxLastMod = dto.LastModified;
                }
            }
            catch (SqlException ex)
            {
                _log.LogError(ex, "Alert SQL query failed after {ElapsedMs}ms", sw.ElapsedMilliseconds);
                return;
            }

            _log.LogDebug(
                "Loaded {RowCount} alert changes from SQL in {ElapsedMs}ms",
                changes.Count, sw.ElapsedMilliseconds);

            var filtered = ApplyGroupFilter(changes);

            lock (_lock)
            {
                _changedAlerts = filtered;
                if (maxLastMod > _lastSyncTime)
                    _lastSyncTime = maxLastMod;
            }

            if (filtered.Count > 0)
            {
                _log.LogDebug("{ChangedCount} alerts after filtering", filtered.Count);

                if (!string.IsNullOrEmpty(_settings.AlloyEndpoint))
                    PushToAlloy(filtered);
            }
        }

        private List<AlertDto> ApplyGroupFilter(List<AlertDto> alerts)
        {
            var filter = _resolver?.GetAllowedBmes(_settings.Groups);
            if (filter == null)
                return alerts;

            var result = new List<AlertDto>(alerts.Count);
            foreach (var a in alerts)
            {
                if (a.BaseManagedEntityId.HasValue && filter.Contains(a.BaseManagedEntityId.Value))
                    result.Add(a);
            }
            return result;
        }

        private void PushToAlloy(List<AlertDto> alerts)
        {
            int success = 0;
            int failed = 0;
            var sw = Stopwatch.StartNew();

            foreach (var alert in alerts)
            {
                try
                {
                    var json = SerializeAlert(alert);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var response = _httpClient.PostAsync(_settings.AlloyEndpoint, content).Result;

                    if (response.IsSuccessStatusCode)
                    {
                        success++;
                        _log.LogTrace(
                            "Pushed alert {AlertId} to Alloy ({StatusCode})",
                            alert.AlertId, (int)response.StatusCode);
                    }
                    else
                    {
                        failed++;
                        _log.LogWarning(
                            "Alloy rejected alert {AlertId}: {StatusCode} {ReasonPhrase}",
                            alert.AlertId, (int)response.StatusCode, response.ReasonPhrase);
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    _log.LogError(ex,
                        "Exception pushing alert {AlertId} to Alloy at {Endpoint}",
                        alert.AlertId, _settings.AlloyEndpoint);
                }
            }

            _log.LogDebug(
                "Alloy push completed: {Success} succeeded, {Failed} failed in {ElapsedMs}ms",
                success, failed, sw.ElapsedMilliseconds);
        }

        private static AlertDto ReadRow(SqlDataReader r)
        {
            int severity = r.IsDBNull(3) ? 0 : Convert.ToInt32(r.GetValue(3));
            int resState = r.IsDBNull(5) ? 0 : Convert.ToInt32(r.GetValue(5));

            return new AlertDto
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
                BaseManagedEntityId = r.IsDBNull(28) ? (Guid?)null : r.GetGuid(28),
                EntityDisplayName = GetStringOrEmpty(r, 29),
                EntityFullName = GetStringOrEmpty(r, 30)
            };
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
                base_managed_entity_id = a.BaseManagedEntityId?.ToString(),
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

        private static string GetStringOrEmpty(SqlDataReader r, int index)
        {
            return r.IsDBNull(index) ? "" : r.GetString(index);
        }
    }
}
