namespace Cockpit.Core.Configuration;

// The product name and the addresses that carry it (AC-508).
// The onboarding screens are the most name-bearing surface this app has, and the guide lives on the website
// rather than in the app — so a screen text, a menu item and a sign-in step all need to say the same name and
// point at the same domain. AC-167 has not settled either yet: the name leans towards Wispslate and the
// canonical domain (`.com` or `.app`) is still open. Two hand-kept copies of a domain drift, and a
// domain that drifts after a client is registered under it is worse than one that was never written down, so
// every surface resolves both from here and the change stays a one-line change.
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
