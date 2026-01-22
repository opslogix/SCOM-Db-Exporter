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
  "Http": {
    "Host": "localhost",
    "Port": 9464
  },
  "Modules": {
    "Metrics": {
      "Enabled": true,
      "PollSeconds": 30
    },
    "State": {
      "Enabled": true,
      "PollSeconds": 30
    },
    "Alert": {
      "Enabled": true,
      "PollSeconds": 30,
      "IncludeClosedAlerts": false,
      "ClosedAlertRetentionMinutes": 60
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

### Module Settings

| Module | Setting | Description |
|--------|---------|-------------|
| **Metrics** | `Enabled` | Enable/disable performance metrics collection |
|  | `PollSeconds` | How often to query SCOM for new performance data |
| **State** | `Enabled` | Enable/disable entity health state collection |
|  | `PollSeconds` | How often to query SCOM for state changes |
| **Alert** | `Enabled` | Enable/disable alert collection |
|  | `PollSeconds` | How often to query SCOM for alert changes |
|  | `IncludeClosedAlerts` | Include closed alerts (ResolutionState=255) in queries |
|  | `ClosedAlertRetentionMinutes` | How long to track closed alerts before removing from memory |

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

## Alert Endpoint Details

The `/alerts` endpoint returns JSON data designed for ingestion into Loki via Grafana Alloy. It includes:

- All standard alert fields (name, description, severity, priority, resolution state, etc.)
- Custom fields 1-10
- Entity information (display name, full name)
- Timestamps (time raised, time added, last modified, time resolved)
- Alert context XML

### Deduplication

The alert exporter includes built-in deduplication:
- Only **new or modified** alerts are returned on each request
- Alerts are tracked by their `LastModified` timestamp
- Closed alerts are cleaned up after `ClosedAlertRetentionMinutes`
- On service restart, all current alerts are emitted once

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
