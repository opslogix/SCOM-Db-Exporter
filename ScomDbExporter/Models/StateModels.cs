using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ScomDbExporter.Models
{
    public sealed class EntityStateDto
    {
        public string DisplayName { get; set; }
        public string FullName { get; set; }
        public int HealthState { get; set; }
        public string HealthText { get; set; }
    }
}
