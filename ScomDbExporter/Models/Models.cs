using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Prometheus;

namespace ScomDbExporter.Models
{
    // You can move these model classes to Models/Models.cs
    public class CounterInfo { public Guid PerformanceCounterId; public string CounterName; public string ObjectName; public string LookupKey; }
    public class EntityInfo { public Guid BaseManagedEntityId; public string DisplayName; public string Path; public string FullName; }
    public class PerfSample { public double Value; public DateTime Timestamp; public CounterInfo Counter; public EntityInfo Entity; }
    public class MappingFile { public List<MappingEntry> Mappings { get; set; } }
    public class MappingEntry { public string ObjectName; public string CounterName; public string MetricName; public Dictionary<string, string> Labels; public double ValueMultiplier; }
    public class MetricDefinition { public string MetricName; public string[] LabelNames; public Gauge Gauge; }
}
