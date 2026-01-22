using System;

namespace ScomDbExporter.Models
{
    public sealed class AlertDto
    {
        public Guid AlertId { get; set; }
        public string AlertName { get; set; }
        public string AlertDescription { get; set; }
        public int Severity { get; set; }
        public string SeverityText { get; set; }
        public int Priority { get; set; }
        public int ResolutionState { get; set; }
        public string ResolutionStateText { get; set; }
        public string Category { get; set; }
        public DateTime TimeRaised { get; set; }
        public DateTime TimeAdded { get; set; }
        public DateTime LastModified { get; set; }
        public DateTime? TimeResolved { get; set; }
        public int RepeatCount { get; set; }
        public string Owner { get; set; }
        public string ResolvedBy { get; set; }
        public string TicketId { get; set; }
        public string Context { get; set; }
        public string CustomField1 { get; set; }
        public string CustomField2 { get; set; }
        public string CustomField3 { get; set; }
        public string CustomField4 { get; set; }
        public string CustomField5 { get; set; }
        public string CustomField6 { get; set; }
        public string CustomField7 { get; set; }
        public string CustomField8 { get; set; }
        public string CustomField9 { get; set; }
        public string CustomField10 { get; set; }
        public string EntityDisplayName { get; set; }
        public string EntityFullName { get; set; }
        public bool IsMonitorAlert { get; set; }
        public Guid? ConnectorId { get; set; }
    }
}
