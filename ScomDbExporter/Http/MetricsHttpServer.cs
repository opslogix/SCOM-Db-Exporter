using Prometheus;

namespace ScomDbExporter.Http
{
    internal sealed class MetricsHttpServer
    {
        private readonly MetricServer _server;

        public MetricsHttpServer(string host, int port)
        {
            _server = new MetricServer(host, port);
        }

        public void Start()
        {
            _server.Start();
        }
    }
}
