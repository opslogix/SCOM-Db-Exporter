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

        private GroupMembershipResolver _resolver;
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

            _resolver = new GroupMembershipResolver(
                _config.ConnectionString,
                _config.GroupResolver,
                CollectAllConfiguredGroups(),
                _loggerFactory.CreateLogger<GroupMembershipResolver>());

            _resolver.Init();

            var perf = new PerformanceExporter(
                _config.ConnectionString,
                _config.Modules.Metrics,
                _resolver,
                _loggerFactory.CreateLogger<PerformanceExporter>());

            if (perf.Enabled)
            {
                _modules.Add(perf);
                _log.LogInformation(
                    "PerformanceExporter enabled (PollSeconds={PollSeconds}, Groups={Groups})",
                    _config.Modules.Metrics.PollSeconds,
                    FormatGroups(_config.Modules.Metrics.Groups));
            }

            var state = new StateExporter(
                _config.ConnectionString,
                _config.Modules.State,
                _resolver,
                _loggerFactory.CreateLogger<StateExporter>());

            if (state.Enabled)
            {
                _modules.Add(state);
                _log.LogInformation(
                    "StateExporter enabled (PollSeconds={PollSeconds}, FullReconcileMin={FullReconcile}, Groups={Groups})",
                    _config.Modules.State.PollSeconds,
                    _config.Modules.State.FullReconcileMinutes,
                    FormatGroups(_config.Modules.State.Groups));
            }

            var alert = new AlertExporter(
                _config.ConnectionString,
                _config.Modules.Alert,
                _resolver,
                _loggerFactory.CreateLogger<AlertExporter>());

            if (alert.Enabled)
            {
                _modules.Add(alert);
                _log.LogInformation(
                    "AlertExporter enabled (PollSeconds={PollSeconds}, IncludeClosedAlerts={IncludeClosed}, AlloyEndpoint={AlloyEndpoint}, Groups={Groups})",
                    _config.Modules.Alert.PollSeconds,
                    _config.Modules.Alert.IncludeClosedAlerts,
                    _config.Modules.Alert.AlloyEndpoint ?? "(none)",
                    FormatGroups(_config.Modules.Alert.Groups));
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
                try
                {
                    _resolver?.Tick();
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Group resolver tick failed");
                }

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

        private IEnumerable<string> CollectAllConfiguredGroups()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddNames(set, _config.Modules?.Metrics?.Groups);
            AddNames(set, _config.Modules?.State?.Groups);
            AddNames(set, _config.Modules?.Alert?.Groups);
            return set;
        }

        private static void AddNames(HashSet<string> set, string[] names)
        {
            if (names == null) return;
            foreach (var n in names)
            {
                if (!string.IsNullOrWhiteSpace(n))
                    set.Add(n.Trim());
            }
        }

        private static string FormatGroups(string[] groups)
        {
            if (groups == null || groups.Length == 0) return "(none)";
            return "[" + string.Join(", ", groups) + "]";
        }

        public void Stop()
        {
            _log.LogInformation("Stopping ScomDbExporter");

            _running = false;

            _log.LogInformation("ScomDbExporter stopped");
        }
    }
}
