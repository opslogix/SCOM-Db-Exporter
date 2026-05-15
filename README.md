# SCOM-Db-Exporter
Exports SCOM Monitoring data in Prometheus and Loki format

The ZIP file contains two main executables:
- **ScomDbExporter.Service.exe** – intended to run as a Windows service
- **ScomDbExporter.Console.exe** – intended for manual execution from the command line (useful for testing and troubleshooting)

## Service Installation

The service can be created using the following command:

```
sc create ScomDbExporterService binPath= "C:\Program Files\ScomDbExporter\ScomDbExporter.Service.exe" start= auto DisplayName= "SCOM Database Exporter"
```

Both executables require **.NET Framework 4.7.2 or 4.8**. This requirement aligns the exporter with the .NET framework version used by SCOM itself.

## Configuration

To configure the exporter, edit the `appsettings.json` file included in the package.

```json
{
  "ConnectionString": "Data Source=your-sql-server;Initial Catalog=OperationsManager;Integrated Security=True;",
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "System": "Warning"
      }
    },
    "Enrich": [ "FromLogContext" ]
  },
  "Http": {
    "Host": "localhost",
    "Port": 9464
  },
  "GroupResolver": {
    "RefreshMinutes": 15,
    "MaxNestedDepth": 8,
    "IncludeHostedChildren": true
  },
  "Modules": {
    "Metrics": {
      "Enabled": true,
      "PollSeconds": 30,
      "Groups": []
    },
    "State": {
      "Enabled": true,
      "PollSeconds": 30,
      "FullReconcileMinutes": 10,
      "Groups": []
    },
    "Alert": {
      "Enabled": true,
      "PollSeconds": 30,
      "IncludeClosedAlerts": false,
      "AlloyEndpoint": "http://localhost:9465/loki/api/v1/raw",
      "Groups": []
    }
  }
}
```

### Configuration Options

| Setting | Description |
|---------|-------------|
| `ConnectionString` | SQL Server connection string pointing to your OperationsManager database |
| `Http.Host` | Host binding (use `localhost` for local only, or `+` for all interfaces) |
| `Http.Port` | Port number for all HTTP endpoints (default: 9464) |

### Group Resolver Settings

The `GroupResolver` block controls how SCOM group memberships are resolved when one or more modules declare `Groups` (see [Filtering by SCOM Groups](#filtering-by-scom-groups)).

| Setting | Default | Description |
|---------|---------|-------------|
| `GroupResolver.RefreshMinutes` | `15` | How often to re-resolve group membership against the SCOM DB. This is the only periodic group-related query — keep it relatively rare. |
| `GroupResolver.MaxNestedDepth` | `8` | Maximum nesting depth when walking groups that contain groups. |
| `GroupResolver.IncludeHostedChildren` | `true` | When `true`, automatically includes every entity whose `TopLevelHostEntity` is a group member. Lets a `Servers` group also match each server's CPU/disk/process child entities. |

### Module Settings

| Module | Setting | Description |
|--------|---------|-------------|
| **Metrics** | `Enabled` | Enable/disable performance metrics collection |
|  | `PollSeconds` | How often to query SCOM for new performance data |
|  | `Groups` | Optional `string[]` of SCOM group display names to filter to. Omitted, `null`, or `[]` means **no filter** — every entity is exported (default). See [Filtering by SCOM Groups](#filtering-by-scom-groups). |
| **State** | `Enabled` | Enable/disable entity health state collection |
|  | `PollSeconds` | How often to query SCOM for state changes (incremental on `LastModified`) |
|  | `FullReconcileMinutes` | How often to run a full state query instead of an incremental one. The full query prunes entries that no longer exist in SCOM. Default `10`. |
|  | `Groups` | Optional `string[]` of SCOM group display names to filter to. Omitted, `null`, or `[]` means **no filter** — every entity is exported (default). See [Filtering by SCOM Groups](#filtering-by-scom-groups). |
| **Alert** | `Enabled` | Enable/disable alert collection |
|  | `PollSeconds` | How often to query SCOM for alert changes (incremental on `LastModified`) |
|  | `IncludeClosedAlerts` | Include closed alerts (ResolutionState=255) in queries |
|  | `AlloyEndpoint` | URL of Alloy's loki.source.api endpoint for pushing alerts |
|  | `Groups` | Optional `string[]` of SCOM group display names to filter to. Omitted, `null`, or `[]` means **no filter** — every entity is exported (default). See [Filtering by SCOM Groups](#filtering-by-scom-groups). |

### Logging Settings

The `Serilog` section configures runtime logging. See the [Logging](#logging) section below for what each level emits and how to view Service logs.

| Setting | Description |
|---------|-------------|
| `Serilog.MinimumLevel.Default` | Minimum level emitted: `Verbose`, `Debug`, `Information` (default), `Warning`, `Error`, `Fatal` |
| `Serilog.MinimumLevel.Override` | Per-namespace level overrides, e.g. `"Microsoft": "Warning"` mutes framework noise |
| `Serilog.Enrich` | Serilog enrichers applied to every event (default: `"FromLogContext"`) |

## Networking & Endpoints

By default, the service exposes its endpoints on port **9464**. The exporter exposes three endpoints:

| Endpoint | Format | Description |
|----------|--------|-------------|
| `/metrics` | Prometheus | Performance metrics from SCOM database |
| `/state` | Prometheus | Entity health state (0=Unknown, 1=Healthy, 2=Warning, 3=Critical) |
| `/alerts` | JSON | Alert data for ingestion into Loki |

You can access the endpoints locally via:
- http://localhost:9464/metrics
- http://localhost:9464/state
- http://localhost:9464/alerts

## Logging

Both executables use [Serilog](https://serilog.net/) configured via the `Serilog` section in `appsettings.json`.

| Host | Sink | Where to look |
|------|------|---------------|
| `ScomDbExporter.Console.exe` | Console | stdout (the terminal window) |
| `ScomDbExporter.Service.exe` | Windows Event Log | **Application** log, source `ScomDbExporter` |

The Service writes to the existing Windows **Application** log — no new log is created. The first time the service starts it registers its event source (`ScomDbExporter`) automatically; this requires the service account to have administrator rights on first launch only.

### Changing the log level

Edit `Serilog.MinimumLevel.Default` in `appsettings.json`. Restart the host to apply.

| Level | Use case |
|-------|----------|
| `Information` (default) | Normal operation: lifecycle events, module init, errors |
| `Debug` | Troubleshooting: per-poll row counts and elapsed ms, per-request HTTP traces, Alloy push tallies |
| `Verbose` | Very chatty: per-alert Alloy POST traces |

You can also override per namespace via `Serilog.MinimumLevel.Override`, e.g. set only the Alert module to `Debug`:

```json
"Override": {
  "Microsoft": "Warning",
  "System": "Warning",
  "ScomDbExporter.Modules.AlertExporter": "Debug"
}
```

### Viewing Service logs

Open **Event Viewer** → **Windows Logs** → **Application**, then filter by source `ScomDbExporter`. From PowerShell:

```powershell
Get-EventLog -LogName Application -Source ScomDbExporter -Newest 50
```

## Filtering by SCOM Groups

Each module accepts an optional `Groups` array of SCOM group display names. When set, only entities that belong to those groups (and, by default, their hosted child entities) are exported.

```json
"State": {
  "Enabled": true,
  "PollSeconds": 30,
  "Groups": [ "Production Servers", "SQL Servers" ]
}
```

### When `Groups` is empty

`Groups` is an **allowlist**, not a deny-by-default switch. The three states are:

| `Groups` value | Behavior |
|----------------|----------|
| Omitted, `null`, or `[]` | **No filter** — every entity is exported (default behavior, identical to the exporter before this feature existed). |
| `["Production Servers"]` | Only members of that group (plus hosted children if `IncludeHostedChildren` is `true`) are exported. |
| `["NonExistentGroup"]` | The group resolves to zero entities → a warning is logged at startup, and the module exports **nothing**. |

If every module's `Groups` is empty/omitted, the resolver does not run the SCOM resolution query at all and logs `Group resolver: no groups configured — filtering disabled` at startup.

### How it works

- A shared `GroupMembershipResolver` resolves the configured group display names to a set of `BaseManagedEntityId`s using SCOM's `Relationship` table and `MTV_Group` view.
- Resolution runs once at startup and again every `GroupResolver.RefreshMinutes` — this is the only periodic group-related SQL query the exporter issues.
- Each module's poll fetches data as usual and then filters in-memory against the cached BME set. Filtering does **not** add cost to the per-poll SCOM query.
- Nested groups (groups that contain other groups) are expanded up to `GroupResolver.MaxNestedDepth` levels.
- If `GroupResolver.IncludeHostedChildren` is `true` (default), every entity whose `TopLevelHostEntity` is a group member is also included. This is what makes filtering by a `Servers` group naturally pick up that server's CPU, disk, OS, and process child entities — which is normally where the perf data and child-monitor state lives.

If multiple modules reference different groups, they are resolved together — one DB round-trip per refresh, regardless of how many modules use grouping.

## Alert Endpoint Details

The `/alerts` endpoint returns JSON data designed for ingestion into Loki via Grafana Alloy. It includes:

- All standard alert fields (name, description, severity, priority, resolution state, etc.)
- Custom fields 1-10
- Entity information (`base_managed_entity_id`, display name, full name)
- Timestamps (time raised, time added, last modified, time resolved)
- Alert context XML

### Incremental delivery

The alert exporter only fetches and emits **changes**:
- The SQL query is incremental on `LastModified` — at each tick only rows whose `LastModified` has advanced since the previous tick are fetched.
- The first poll after startup loads all currently-open alerts as the bootstrap snapshot, then the exporter stays incremental until restart.
- When an alert closes, its `LastModified` advances and it is emitted exactly once with `resolution_state = 255` (provided `IncludeClosedAlerts = true`; otherwise closures are suppressed).
- The `/alerts` HTTP endpoint and the Alloy push share the same delta — both emit the latest poll's changed alerts.

## Grafana Alloy Configuration

### Metrics and State (Prometheus format)

```alloy
prometheus.scrape "scom_metrics" {
  targets = [
    {
      __address__      = "localhost:9464",
      __metrics_path__ = "/metrics",
    },
  ]
  scrape_interval = "15s"
  honor_labels = true
  forward_to      = [prometheus.remote_write.mimir.receiver]
}

prometheus.scrape "scom_state" {
  targets = [
    {
      __address__      = "localhost:9464",
      __metrics_path__ = "/state",
    },
  ]
  scrape_interval = "30s"
  forward_to      = [prometheus.remote_write.mimir.receiver]
}

prometheus.remote_write "mimir" {
  endpoint {
    url = "http://your-mimir-server:9009/api/v1/push"
  }
}
```

### Alerts (JSON to Loki)

```alloy
loki.source.api "scom_alerts" {
  http {
    listen_address = "127.0.0.1"
    listen_port    = 9465
  }
  forward_to = [loki.process.scom_alerts.receiver]
}

loki.process "scom_alerts" {
  forward_to = [loki.write.default.receiver]

  stage.json {
    expressions = {
      severity = "severity_text",
      state    = "resolution_state_text",
      category = "category",
    }
  }

  stage.labels {
    values = {
      severity = "",
      state    = "",
      category = "",
    }
  }

  stage.static_labels {
    values = {
      source = "scom",
      job    = "scom-alerts",
    }
  }

  stage.timestamp {
    source = "time_raised"
    format = "2006-01-02T15:04:05.000Z"
  }
}

loki.write "default" {
  endpoint {
    url = "http://your-loki-server:3100/loki/api/v1/push"
  }
}
```

### Label Cardinality Warning

When configuring Alloy for the alerts endpoint, only use **low-cardinality fields** as Loki labels:

| Safe as Labels | NOT Safe as Labels |
|----------------|-------------------|
| `severity_text` (4 values) | `alert_id` (unique per alert!) |
| `resolution_state_text` (~10 values) | `alert_name` (many types) |
| `category` (limited set) | `entity_display_name` (thousands of servers) |
|  | `ticket_id`, `owner`, `context` |

High-cardinality labels will cause Loki performance issues. Keep detailed fields in the log body and query them with LogQL's JSON parser.
