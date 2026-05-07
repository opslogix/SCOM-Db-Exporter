using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Serilog;
using ScomDbExporter.Config;
using ScomDbExporter.Core;
using System;
using System.IO;
using System.ServiceProcess;

namespace ScomDbExporter.Service
{
    public sealed partial class ExporterService : ServiceBase
    {
        private ExporterHost _host;
        private ILoggerFactory _loggerFactory;

        public ExporterService()
        {
            ServiceName = "ScomDbExporter";
        }

        protected override void OnStart(string[] args)
        {
            var basePath = AppDomain.CurrentDomain.BaseDirectory;
            var settingsPath = Path.Combine(basePath, "appsettings.json");

            var configuration = new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                .Build();

            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .WriteTo.EventLog(
                    source: "ScomDbExporter",
                    logName: "Application",
                    manageEventSource: true)
                .CreateLogger();

            _loggerFactory = LoggerFactory.Create(builder =>
                builder.AddSerilog(Log.Logger, dispose: false));

            var cfg = JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(settingsPath));

            _host = new ExporterHost(cfg, _loggerFactory);
            _host.Start();
        }

        protected override void OnStop()
        {
            _host?.Stop();
            _loggerFactory?.Dispose();
            Log.CloseAndFlush();
        }
    }
}
