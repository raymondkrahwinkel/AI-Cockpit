namespace Cockpit.Plugins.Abstractions.HeavyRuns;

// AC-1535: why a heavy run is wanted. A gate right before a merge goes ahead of an exploring run. `Operator` is a
// run the operator started by hand: it never waits, but it holds a place, so agents' runs wait for it, and it shows.
public enum HeavyRunPurpose
{
    Explore,
    PreMerge,
    Operator,
}
