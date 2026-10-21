namespace Cockpit.Plugins.Abstractions.HeavyRuns;

// AC-1535: one ask for a place in the machine-wide queue. `PaneId` owns the place: when that session goes away, the
// place is given back and the run is ended. `WorkingDirectory` names the repository the run's cost is learned for.
public sealed record HeavyRunRequest(
    string RunId,
    HeavyRunKind Kind,
    HeavyRunPurpose Purpose,
    string PaneId,
    string Label,
    string WorkingDirectory);
