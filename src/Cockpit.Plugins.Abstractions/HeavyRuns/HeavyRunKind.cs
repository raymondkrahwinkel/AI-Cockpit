namespace Cockpit.Plugins.Abstractions.HeavyRuns;

// AC-1535: what a run is, as far as the machine-wide queue cares. `Light` (lint, one filtered test class) passes
// the queue by; every other kind waits for room. Each kind learns its own cost per repository.
public enum HeavyRunKind
{
    Light,
    Build,
    Unit,
    Journeys,
    Gate,
}
