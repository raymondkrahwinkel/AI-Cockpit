namespace Cockpit.Core.Diagnostics;

// One process, as every platform can describe it (#78): who it is, who spawned it, what it has burned and what
// it occupies. The three platform readers all produce this, so the part that matters — walking the tree and
// turning two samples into a percentage — is written once and tested without a process in sight.
//
// `Name`: The executable's own name ("ollama", "claude") — what a local model server is recognised by, since it runs beside the cockpit rather than under it.
public sealed record ProcessRow(int ProcessId, int ParentProcessId, TimeSpan CpuTime, long WorkingSetBytes, string Name = "");
