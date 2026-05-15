namespace ScomDbExporter.Config
{
    public class AppConfig
    {
        public string ConnectionString { get; set; }
        public HttpConfig Http { get; set; } = new HttpConfig();
        public ModuleConfig Modules { get; set; } = new ModuleConfig();
        public GroupResolverConfig GroupResolver { get; set; } = new GroupResolverConfig();
    }

    public class HttpConfig
    {
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 9464;
    }

    public class ModuleConfig
    {
        public ModuleToggle Metrics { get; set; } = new ModuleToggle();
        public StateModuleToggle State { get; set; } = new StateModuleToggle { PollSeconds = 30 };
        public AlertModuleToggle Alert { get; set; } = new AlertModuleToggle();
    }

    public class ModuleToggle
    {
        public bool Enabled { get; set; } = true;
        public int PollSeconds { get; set; } = 5;

        // SCOM group display names. Null/empty = no filter.
        public string[] Groups { get; set; }
    }

    public class StateModuleToggle : ModuleToggle
    {
        // How often to run a full reconcile (rather than incremental) to prune deleted entities.
        public int FullReconcileMinutes { get; set; } = 10;
    }

    public class AlertModuleToggle : ModuleToggle
    {
        public bool IncludeClosedAlerts { get; set; } = false;
        public int ClosedAlertRetentionMinutes { get; set; } = 60;
        public string AlloyEndpoint { get; set; } = "http://localhost:9465/loki/api/v1/raw";
    }

    public class GroupResolverConfig
    {
        // Refresh cadence for group membership. Resolution is the only DB-heavy
        // operation introduced by grouping; keep this rare.
        public int RefreshMinutes { get; set; } = 15;

        // Cap for nested-group traversal depth. Most SCOM topologies need <= 8.
        public int MaxNestedDepth { get; set; } = 8;

        // When true, includes all entities whose TopLevelHostEntity is a group member.
        // Typical use: filter by a "Servers" group and pick up CPU/Disk/Process child
        // entities hosted by those servers.
        public bool IncludeHostedChildren { get; set; } = true;
    }
}
