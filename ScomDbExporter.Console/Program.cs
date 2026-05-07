using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Serilog;
using ScomDbExporter.Config;
using ScomDbExporter.Core;
using System;
using System.IO;

internal static class Program
{
    static void Main()
    {
        var basePath = AppDomain.CurrentDomain.BaseDirectory;
        var settingsPath = Path.Combine(basePath, "appsettings.json");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .WriteTo.Console(outputTemplate:
                "{Timestamp:HH:mm:ss} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            using var loggerFactory = LoggerFactory.Create(builder =>
                builder.AddSerilog(Log.Logger, dispose: false));

            var cfg = JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(settingsPath));

            var host = new ExporterHost(cfg, loggerFactory);
            host.Start();

            Console.WriteLine("Running... press ENTER to stop");
            Console.ReadLine();

            host.Stop();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Host terminated unexpectedly");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
