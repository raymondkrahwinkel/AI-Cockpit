namespace Cockpit.Core.HeavyRuns;

// AC-1535: `Admit` is the index of the waiting run that may start now; otherwise `WaitReason` says why the run at
// the head of the line may not. Both null when nothing waits.
public sealed record HeavyRunDecision(int? Admit, string? WaitReason);
