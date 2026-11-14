namespace Cockpit.Core.Diagnostics;

// AC-1013 (was #78): One process as every platform describes it, so tree-walking and CPU% math are written once.
// `Name` is the executable's own name ("ollama", "claude"). AC-2353: `StartedAt` tells a reused pid from the
// process that held it before; null where the platform reader cannot say.
public sealed record ProcessRow(int ProcessId, int ParentProcessId, TimeSpan CpuTime, long WorkingSetBytes, string Name = "", DateTimeOffset? StartedAt = null);
