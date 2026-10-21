namespace Cockpit.Plugins.Abstractions.HeavyRuns;

// AC-1535: why a heavy run is wanted. A gate right before a merge goes ahead of an exploring run.
public enum HeavyRunPurpose
{
    Explore,
    PreMerge,
}
