namespace Cockpit.Core.Updates;

// Which builds this cockpit is willing to be told about (#71). The stream half of a channel name (AC-387).
public enum UpdateChannel
{
    // Tagged releases only — `v1.2.3`. A cockpit that quietly moves onto last night's build is not one you can trust with a day's work.
    Stable,

    // Also the rolling nightly build of main. Opt-in, and it means what it says: main, as it was last night.
    Nightly,
}

// A build on offer, as the update feed has it (#71, AC-387).
//
// Everything here is something Velopack's feed actually carries. It notably does not carry a publication date or a
// release title: the feed is a list of packages, not a page. What the operator reads is built from the version, and
// the link is derived from the tag the workflow published under — see `VelopackUpdateService`.
//
// `Version`: The version on offer — `0.9.0`, or `0.8.0-nightly.124` on a nightly channel. This is a build's whole identity: the nightly tag rolls, the version does not repeat.
// `Notes`: The release notes packed into the feed — what changed.
// `Url`: The release page, for an operator who would rather read it in a browser.
public sealed record AppRelease(string Version, string Notes, string Url);

// What a check found: a build worth telling the operator about, or nothing.
//
// `Release`: The newer build, or null when this cockpit is current.
// `Failure`: Why the check could not be made, or null. A check that failed is not an "up to date" — saying so would be a lie the operator would believe.
public sealed record UpdateCheckResult(AppRelease? Release, string? Failure)
{
    public bool HasUpdate => Release is not null;

    public static UpdateCheckResult UpToDate => new(null, null);

    public static UpdateCheckResult Failed(string why) => new(null, why);
}

// What a download attempt did (AC-388). Its own type rather than reusing `UpdateCheckResult`: a check
// finding nothing is an ordinary "up to date", but a download finding nothing to fetch — or losing the network
// partway through — must never look like the success a check's null `UpdateCheckResult.Release` does.
// An aborted or failed download is expected to leave the app exactly as it found it; this is what says so happened,
// so the caller neither applies a build that never arrived nor clears the offer that is still good.
//
// `Succeeded`: Whether the build now on offer finished downloading intact.
// `Failure`: Why it did not, or null on success. Never "up to date"/"installed" wording — this call was never asking that question.
public sealed record UpdateDownloadResult(bool Succeeded, string? Failure)
{
    public static UpdateDownloadResult Ok() => new(true, null);

    public static UpdateDownloadResult Failed(string why) => new(false, why);
}
