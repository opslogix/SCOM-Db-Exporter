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

        // How often to reload entity, counter and perf-source caches from the DB.
        // Picks up newly discovered objects and removes deleted ones without a restart.
        public int MetadataRefreshMinutes { get; set; } = 30;

        // Metrics module: samples reach the DB seconds to minutes after TimeSampled.
        // Each poll therefore re-reads this many minutes behind the newest sample
        // seen, so late-arriving rows are not skipped. Must exceed the largest
        // sample-to-insert delay in the environment. 0 = no overlap (legacy behaviour).
        public int PollOverlapMinutes { get; set; } = 15;

        // Metrics module: when no group filter narrows the query, the overlapping
        // catch-up query runs at this cadence instead of on every poll.
        public int CatchUpSeconds { get; set; } = 60;

        // Metrics module: on startup, and when sources are added, load the latest
        // sample per performance source from this many hours back so rarely
        // collected counters are exported immediately. 0 = disabled.
        public int SeedLookbackHours { get; set; } = 48;

        // Metrics module: a series whose newest sample is older than this is removed
        // from the output (a rule that was disabled, or a source that stopped
        // reporting, would otherwise export its last value forever). Must be longer
        // than your slowest collection interval and SeedLookbackHours. 0 = never expire.
        public int SeriesMaxAgeHours { get; set; } = 168;

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

        // When true, includes all entities whose TopLevelHostEntity is a group member.
        // Typical use: filter by a "Servers" group and pick up CPU/Disk/Process child
        // entities hosted by those servers.
        public bool IncludeHostedChildren { get; set; } = true;
    }
}
