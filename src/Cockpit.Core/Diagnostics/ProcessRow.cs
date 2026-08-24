namespace Cockpit.Core.Diagnostics;

// AC-1013 (was #78): One process as every platform can describe it — who it is, who spawned it, what it has
// burned/occupies — so tree-walking and CPU% math are written once and tested without a real process. `Name`
// is the executable's own name ("ollama", "claude"), used to recognize local model servers.
public sealed record ProcessRow(int ProcessId, int ParentProcessId, TimeSpan CpuTime, long WorkingSetBytes, string Name = "");
