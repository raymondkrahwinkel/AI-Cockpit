namespace Cockpit.Core.HeavyRuns;

// AC-1535: one waiting run as admission sees it. `Kind` is the name the wait reason uses ("journeys"); `Cost` is null
// until a run of this kind in this repository was measured; `FreeDiskBytes` is the volume the run works on.
public sealed record HeavyRunCandidate(
    string Kind,
    bool IsPreMerge,
    DateTimeOffset EnqueuedAt,
    HeavyRunCost? Cost,
    long? FreeDiskBytes);
