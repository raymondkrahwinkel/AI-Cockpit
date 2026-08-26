namespace Cockpit.Core.Configuration;

// Product name and addresses live here (AC-508): onboarding and the web guide must agree while AC-167 settles them.
// One source prevents domain drift, which can invalidate an already-registered client.
// Screens, menus, sign-in, and the external guide therefore resolve both values from this class.
public static class CockpitBrand
{
    // The product name as it appears to the operator.
    public const string ProductName = "Wispslate Cockpit";

    // Where the guide lives; placeholder until AC-167 picks the canonical domain. AC-512: the site describes the
    // newest release, not the host reading it — accepted rather than versioned, so an older host may read ahead of itself.
    public const string GuideUrl = "https://wispslate.app/guide";

    // The Depot the first-run wizard offers to sign in to. Placeholder until AC-167.
    public const string DepotUrl = "https://depot.wispslate.app";
}
