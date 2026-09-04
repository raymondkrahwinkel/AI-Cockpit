namespace Cockpit.Core.Configuration;

// Product name and addresses live here (AC-508). One source prevents domain drift, which can invalidate an
// already-registered client, so screens, menus, sign-in and the external guide all resolve both values here.
// AC-167 is closed: the name is settled and both domains are registered, .app primary. What is not, is below.
public static class CockpitBrand
{
    // The product name as it appears to the operator — carried through in the interface only (AC-430). The README,
    // the repo, the installer and the package-ids still read AI-Cockpit/Cockpit; finishing that is AC-429[c], and
    // it is what AC-306 (the SDK on nuget.org) waits on, since a package-id is public and permanent.
    public const string ProductName = "Wispslate Cockpit";

    // Where the guide lives. The address is decided; standing the site up under it is AC-186, under that same
    // AC-429[c]. AC-512: it describes the newest release, not the host reading it — accepted rather than
    // versioned, so an older host may read ahead of itself.
    public const string GuideUrl = "https://wispslate.app/guide";

    // The Depot the first-run wizard offers to sign in to. Same standing as GuideUrl: the address is decided,
    // deploying a Depot under it is AC-429[c].
    public const string DepotUrl = "https://depot.wispslate.app";
}
