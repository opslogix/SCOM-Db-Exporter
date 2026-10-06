using System;

namespace ScomDbExporter.Models
{
    public sealed class EntityStateDto
    {
        public Guid BaseManagedEntityId { get; set; }
        public string DisplayName { get; set; }
        public string FullName { get; set; }
        public int HealthState { get; set; }
        public string HealthText { get; set; }
    }

    public sealed class MonitorStateDto
    {
        public Guid BaseManagedEntityId { get; set; }
        public Guid MonitorId { get; set; }
        public string MonitorName { get; set; }
        public string DisplayName { get; set; }
        public string FullName { get; set; }
        public int HealthState { get; set; }
    }
}
