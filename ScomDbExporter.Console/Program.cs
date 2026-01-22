using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using ScomDbExporter.Config;
using ScomDbExporter.Core;
using System;
using System.IO;

internal static class Program
{
    static void Main()
    {
        var loggerFactory = CreateLoggerFactory();
        var logger = loggerFactory.CreateLogger<ExporterHost>();

        var cfg = JsonConvert.DeserializeObject<AppConfig>(
            File.ReadAllText("appsettings.json"));

        var host = new ExporterHost(cfg, logger);
        host.Start();

        Console.WriteLine("Running... press ENTER to stop");
        Console.ReadLine();

        host.Stop();
        loggerFactory.Dispose();
    }

    private static ILoggerFactory CreateLoggerFactory()
    {
        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
        });
    }
}
