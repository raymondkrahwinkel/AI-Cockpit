namespace Cockpit.Core.Configuration;

/// <summary>
/// The product name and the addresses that carry it (AC-508).
/// </summary>
/// <remarks>
/// The onboarding screens are the most name-bearing surface this app has, and the guide lives on the website
/// rather than in the app — so a screen text, a menu item and a sign-in step all need to say the same name and
/// point at the same domain. AC-167 has not settled either yet: the name leans towards Wispslate and the
/// canonical domain (<c>.com</c> or <c>.app</c>) is still open. Two hand-kept copies of a domain drift, and a
/// domain that drifts after a client is registered under it is worse than one that was never written down, so
/// every surface resolves both from here and the change stays a one-line change.
/// </remarks>
public static class CockpitBrand
{
    /// <summary>The product name as it appears to the operator.</summary>
    public const string ProductName = "Wispslate Cockpit";

    /// <summary>Where the guide lives. Placeholder until AC-167 picks the canonical domain.</summary>
    public const string GuideUrl = "https://wispslate.app/guide";

    /// <summary>The Depot the first-run wizard offers to sign in to. Placeholder until AC-167.</summary>
    public const string DepotUrl = "https://depot.wispslate.app";
}
