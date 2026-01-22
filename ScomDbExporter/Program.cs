using Microsoft.Extensions.Logging;
using ScomDbExporter.Config;
using ScomDbExporter.Http;
using ScomDbExporter.Modules;
using System;
using System.Collections.Generic;
using System.Threading;

namespace ScomDbExporter.Core
{
    public sealed class ExporterHost
    {
        private readonly AppConfig _config;
        private readonly ILogger<ExporterHost> _log;
        private readonly List<IExporterModule> _modules = new();

        private MetricsHttpServer? _metrics;
        private StateHttpServer? _stateServer;
        private AlertHttpServer? _alertServer;

        private volatile bool _running;
        private AppConfig cfg;

        public ExporterHost(AppConfig config, ILogger<ExporterHost> log)
        {
            _config = config;
            _log = log;
        }

        public ExporterHost(AppConfig cfg)
        {
            this.cfg = cfg;
        }

        public void Start()
        {
            _log.LogInformation("Starting ScomDbExporter");

            // metrics
            _metrics = new MetricsHttpServer(
                _config.Http.Host,
                _config.Http.Port);

            _metrics.Start();
            _log.LogInformation(
                "Metrics endpoint started on {Host}:{Port}",
                _config.Http.Host,
                _config.Http.Port);

            // modules
            var perf = new PerformanceExporter(
                _config.ConnectionString,
                _config.Modules.Metrics);

            if (perf.Enabled)
            {
                _modules.Add(perf);
                _log.LogInformation("PerformanceExporter enabled");
            }

            var state = new StateExporter(
                _config.ConnectionString,
                _config.Modules.State);

            if (state.Enabled)
            {
                _modules.Add(state);
                _log.LogInformation("StateExporter enabled");
            }

            var alert = new AlertExporter(
                _config.ConnectionString,
                _config.Modules.Alert);

            if (alert.Enabled)
            {
                _modules.Add(alert);
                _log.LogInformation("AlertExporter enabled");
            }

            foreach (var m in _modules)
            {
                _log.LogInformation("Initializing module {Module}", m.Name);
                m.Init();
            }

            // state endpoint
            if (state.Enabled)
            {
                _stateServer = new StateHttpServer(
                    state,
                    _config.Http.Port);

                _stateServer.Start();
                _log.LogInformation("State endpoint started");
            }

            // alert endpoint
            if (alert.Enabled)
            {
                _alertServer = new AlertHttpServer(
                    alert,
                    _config.Http.Port);

                _alertServer.Start();
                _log.LogInformation("Alert endpoint started");
            }

            _running = true;

            new Thread(MainLoop)
            {
                IsBackground = true,
                Name = "ExporterMainLoop"
            }.Start();

            _log.LogInformation("ScomDbExporter started successfully");
        }

        private void MainLoop()
        {
            _log.LogInformation("Main loop started");

            while (_running)
            {
                foreach (var m in _modules)
                {
                    try
                    {
                        m.Tick();
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(
                            ex,
                            "Module {Module} tick failed",
                            m.Name);
                    }
                }

                Thread.Sleep(250);
            }

            _log.LogInformation("Main loop stopped");
        }

        public void Stop()
        {
            _log.LogInformation("Stopping ScomDbExporter");

            _running = false;

            // _metrics?.Stop();
            // _stateServer?.Stop();

            _log.LogInformation("ScomDbExporter stopped");
        }
    }
}
