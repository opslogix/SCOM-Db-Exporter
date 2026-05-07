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
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<ExporterHost> _log;
        private readonly List<IExporterModule> _modules = new();

        private MetricsHttpServer? _metrics;
        private StateHttpServer? _stateServer;
        private AlertHttpServer? _alertServer;

        private volatile bool _running;

        public ExporterHost(AppConfig config, ILoggerFactory loggerFactory)
        {
            _config = config;
            _loggerFactory = loggerFactory;
            _log = loggerFactory.CreateLogger<ExporterHost>();
        }

        public void Start()
        {
            _log.LogInformation("Starting ScomDbExporter");

            _metrics = new MetricsHttpServer(
                _config.Http.Host,
                _config.Http.Port,
                _loggerFactory.CreateLogger<MetricsHttpServer>());

            _metrics.Start();
            _log.LogInformation(
                "Metrics endpoint started on {Host}:{Port}",
                _config.Http.Host,
                _config.Http.Port);

            var perf = new PerformanceExporter(
                _config.ConnectionString,
                _config.Modules.Metrics,
                _loggerFactory.CreateLogger<PerformanceExporter>());

            if (perf.Enabled)
            {
                _modules.Add(perf);
                _log.LogInformation("PerformanceExporter enabled (PollSeconds={PollSeconds})",
                    _config.Modules.Metrics.PollSeconds);
            }

            var state = new StateExporter(
                _config.ConnectionString,
                _config.Modules.State,
                _loggerFactory.CreateLogger<StateExporter>());

            if (state.Enabled)
            {
                _modules.Add(state);
                _log.LogInformation("StateExporter enabled (PollSeconds={PollSeconds})",
                    _config.Modules.State.PollSeconds);
            }

            var alert = new AlertExporter(
                _config.ConnectionString,
                _config.Modules.Alert,
                _loggerFactory.CreateLogger<AlertExporter>());

            if (alert.Enabled)
            {
                _modules.Add(alert);
                _log.LogInformation(
                    "AlertExporter enabled (PollSeconds={PollSeconds}, IncludeClosedAlerts={IncludeClosed}, AlloyEndpoint={AlloyEndpoint})",
                    _config.Modules.Alert.PollSeconds,
                    _config.Modules.Alert.IncludeClosedAlerts,
                    _config.Modules.Alert.AlloyEndpoint ?? "(none)");
            }

            foreach (var m in _modules)
            {
                _log.LogInformation("Initializing module {Module}", m.Name);
                try
                {
                    m.Init();
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Module {Module} initialization failed", m.Name);
                    throw;
                }
            }

            if (state.Enabled)
            {
                _stateServer = new StateHttpServer(
                    state,
                    _config.Http.Port,
                    _loggerFactory.CreateLogger<StateHttpServer>());

                _stateServer.Start();
                _log.LogInformation("State endpoint started on port {Port}/state", _config.Http.Port);
            }

            if (alert.Enabled)
            {
                _alertServer = new AlertHttpServer(
                    alert,
                    _config.Http.Port,
                    _loggerFactory.CreateLogger<AlertHttpServer>());

                _alertServer.Start();
                _log.LogInformation("Alert endpoint started on port {Port}/alerts", _config.Http.Port);
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
            _log.LogDebug("Main loop started");

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

            _log.LogDebug("Main loop stopped");
        }

        public void Stop()
        {
            _log.LogInformation("Stopping ScomDbExporter");

            _running = false;

            _log.LogInformation("ScomDbExporter stopped");
        }
    }
}
