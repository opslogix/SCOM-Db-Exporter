using System;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using ScomDbExporter.Modules;

namespace ScomDbExporter.Http
{
    internal sealed class AlertHttpServer
    {
        private readonly HttpListener _listener = new();
        private readonly AlertExporter _alert;

        public AlertHttpServer(AlertExporter alert, int port)
        {
            _alert = alert;
            _listener.Prefixes.Add($"http://+:{port}/alerts/");
        }

        public void Start()
        {
            _listener.Start();
            ThreadPool.QueueUserWorkItem(_ => ListenLoop());
        }

        private void ListenLoop()
        {
            while (_listener.IsListening)
            {
                try
                {
                    var ctx = _listener.GetContext();
                    var json = RenderJson();

                    byte[] data = Encoding.UTF8.GetBytes(json);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    ctx.Response.ContentLength64 = data.Length;
                    ctx.Response.OutputStream.Write(data, 0, data.Length);
                    ctx.Response.OutputStream.Close();
                }
                catch
                {
                    // keep server alive
                }
            }
        }

        private string RenderJson()
        {
            var alerts = _alert.CurrentAlerts;

            // Convert to snake_case output format with ISO 8601 dates
            var output = new System.Collections.Generic.List<object>();

            foreach (var a in alerts)
            {
                output.Add(new
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
                });
            }

            return JsonConvert.SerializeObject(output);
        }

        private static string FormatDate(DateTime dt)
        {
            return dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        }
    }
}
