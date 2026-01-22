using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
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
            // 1️⃣ Create logger factory FIRST
            _loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Information);
                builder.AddEventLog(o =>
                {
                    o.SourceName = "ScomDbExporter";
                    o.LogName = "Application";
                });
            });

            var logger = _loggerFactory.CreateLogger<ExporterHost>();

            // 2️⃣ Load config
            var cfgPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "appsettings.json");

            var cfg = JsonConvert.DeserializeObject<AppConfig>(
                File.ReadAllText(cfgPath));

            // 3️⃣ Create and start host
            _host = new ExporterHost(cfg, logger);
            _host.Start();
        }

        protected override void OnStop()
        {
            _host?.Stop();
            _loggerFactory?.Dispose();
        }
    }
}
