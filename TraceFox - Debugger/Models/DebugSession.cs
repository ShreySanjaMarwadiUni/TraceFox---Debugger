using System.Diagnostics;

namespace TraceFox___Debugger.Models
{
    public class DebugSession
    {
        public string SessionId { get; set; } = Guid.NewGuid().ToString();
        public string Code { get; set; } = "";
        public int? ProcessId { get; set; } // <--- NEW
        public bool IsRunning { get; set; }
        public List<StepInfo> Steps { get; set; } = new();
        public int CurrentStep { get; set; } = -1;
        public string ConsoleOutput { get; set; } = "";
        public DateTime LastUpdate { get; set; } = DateTime.Now;

    }

    public class StepInfo
    {
        public int Line { get; set; }
        public string? Filename { get; set; }
        public Dictionary<string, string>? Vars { get; set; }
        public string? Output { get; set; }
        public string? Error { get; set; }
        public bool Finished { get; set; } = false;
    }

}
