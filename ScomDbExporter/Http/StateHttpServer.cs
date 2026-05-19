using System;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using ScomDbExporter.Modules;

namespace ScomDbExporter.Http
{
    internal sealed class StateHttpServer
    {
        private readonly HttpListener _listener = new();
        private readonly StateExporter _state;
        private readonly ILogger<StateHttpServer> _log;

        public StateHttpServer(StateExporter state, string host, int port, ILogger<StateHttpServer> log)
        {
            _state = state;
            _log = log;
            _listener.Prefixes.Add($"http://{host}:{port}/state/");
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
                    var sw = Stopwatch.StartNew();
                    var text = RenderPrometheusText();

                    byte[] data = Encoding.UTF8.GetBytes(text);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "text/plain; version=0.0.4";
                    ctx.Response.ContentLength64 = data.Length;
                    ctx.Response.OutputStream.Write(data, 0, data.Length);
                    ctx.Response.OutputStream.Close();

                    _log.LogDebug(
                        "Served /state ({Bytes} bytes) to {RemoteEndPoint} in {ElapsedMs}ms",
                        data.Length, ctx.Request.RemoteEndPoint, sw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "State endpoint request failed");
                }
            }
        }

        private string RenderPrometheusText()
        {
            var sb = new StringBuilder();

            // IMPORTANT: Prometheus requires LF (\n), NOT CRLF (\r\n)
            sb.Append("# HELP scom_entity_health_state SCOM entity health state (0=Unknown,1=Healthy,2=Warning,3=Critical)\n");
            sb.Append("# TYPE scom_entity_health_state gauge\n");

            foreach (var e in _state.CurrentState)
            {
                sb.Append("scom_entity_health_state");
                sb.Append("{");
                sb.Append("display_name=\"").Append(Escape(e.DisplayName)).Append("\",");
                sb.Append("full_name=\"").Append(Escape(e.FullName)).Append("\"");
                sb.Append("} ");
                sb.Append(e.HealthState);
                sb.Append("\n");
            }

            return sb.ToString();
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n");
        }
    }
}
