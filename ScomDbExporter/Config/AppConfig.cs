namespace ScomDbExporter.Config
{
    public class AppConfig
    {
        public string ConnectionString { get; set; }
        public HttpConfig Http { get; set; } = new HttpConfig();
        public ModuleConfig Modules { get; set; } = new ModuleConfig();
    }

    public class HttpConfig
    {
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 9464;
    }

    public class ModuleConfig
    {
        public ModuleToggle Metrics { get; set; } = new ModuleToggle();
        public ModuleToggle State { get; set; } = new ModuleToggle { PollSeconds = 30 };
        public AlertModuleToggle Alert { get; set; } = new AlertModuleToggle();
    }

    public class ModuleToggle
    {
        public bool Enabled { get; set; } = true;
        public int PollSeconds { get; set; } = 5;
    }

    public class AlertModuleToggle : ModuleToggle
    {
        public bool IncludeClosedAlerts { get; set; } = false;
        public int ClosedAlertRetentionMinutes { get; set; } = 60;
    }
}
