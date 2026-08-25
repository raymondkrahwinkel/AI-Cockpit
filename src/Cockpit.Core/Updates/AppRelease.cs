namespace Cockpit.Core.Updates;

// Which builds this cockpit is willing to be told about (#71). The stream half of a channel name (AC-387).
public enum UpdateChannel
{
    // Tagged releases only — `v1.2.3`. A cockpit that quietly moves onto last night's build is not one you can trust with a day's work.
    Stable,

    // Also the rolling nightly build of main. Opt-in, and it means what it says: main, as it was last night.
    Nightly,
}

// A build on offer, as the update feed has it (#71, AC-387). Everything here is something Velopack's feed
// actually carries — no publication date or release title, since the feed is a list of packages, not a page.
// `Version` is a build's whole identity: the nightly tag rolls, the version does not repeat.
public sealed record AppRelease(string Version, string Notes, string Url);

// What a check found: a build worth telling the operator about, or nothing.
// `Failure`: why the check could not be made, or null — a check that failed is not an "up to date", saying so would be a lie.
public sealed record UpdateCheckResult(AppRelease? Release, string? Failure)
{
    public bool HasUpdate => Release is not null;

    public static UpdateCheckResult UpToDate => new(null, null);

    public static UpdateCheckResult Failed(string why) => new(null, why);
}

// What a download attempt did (AC-388). Its own type rather than reusing `UpdateCheckResult`: a download finding
// nothing to fetch, or losing the network partway through, must never look like the success a check's null
// `Release` does — so the caller neither applies a build that never arrived nor clears an offer that is still good.
public sealed record UpdateDownloadResult(bool Succeeded, string? Failure)
{
    public static UpdateDownloadResult Ok() => new(true, null);

    public static UpdateDownloadResult Failed(string why) => new(false, why);
}
