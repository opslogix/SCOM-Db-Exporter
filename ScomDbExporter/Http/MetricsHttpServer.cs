using Microsoft.Extensions.Logging;
using Prometheus;

namespace ScomDbExporter.Http
{
    internal sealed class MetricsHttpServer
    {
        private readonly MetricServer _server;
        private readonly ILogger<MetricsHttpServer> _log;
        private readonly string _host;
        private readonly int _port;

        public MetricsHttpServer(string host, int port, ILogger<MetricsHttpServer> log)
        {
            _server = new MetricServer(host, port);
            _log = log;
            _host = host;
            _port = port;
        }

        public void Start()
        {
            _server.Start();
            _log.LogDebug("Prometheus MetricServer listening on {Host}:{Port}/metrics", _host, _port);
        }
    }
}
