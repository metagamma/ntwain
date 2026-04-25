using System.Collections.Generic;

namespace Fi6800Scanner.Core.Diagnostics
{
    public class PreflightResult
    {
        public bool Success { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
        public string OsVersion { get; set; }
        public bool IsApp64Bit { get; set; }
        public bool PaperStreamX86Installed { get; set; }
        public bool PaperStreamX64Installed { get; set; }
        public bool TwainDsmAvailable { get; set; }
        public bool ArchitectureMatch { get; set; }
        public string PaperStreamVersion { get; set; }
    }
}
