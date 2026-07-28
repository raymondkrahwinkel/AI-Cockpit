namespace Cockpit.Core.Updates;

/// <summary>Which builds this cockpit is willing to be told about (#71). The stream half of a channel name (AC-387).</summary>
public enum UpdateChannel
{
    /// <summary>Tagged releases only — <c>v1.2.3</c>. A cockpit that quietly moves onto last night's build is not one you can trust with a day's work.</summary>
    Stable,

    /// <summary>Also the rolling nightly build of main. Opt-in, and it means what it says: main, as it was last night.</summary>
    Nightly,
}

/// <summary>
/// A build on offer, as the update feed has it (#71, AC-387).
/// <para>
/// Everything here is something Velopack's feed actually carries. It notably does not carry a publication date or a
/// release title: the feed is a list of packages, not a page. What the operator reads is built from the version, and
/// the link is derived from the tag the workflow published under — see <c>VelopackUpdateService</c>.
/// </para>
/// </summary>
/// <param name="Version">The version on offer — <c>0.9.0</c>, or <c>0.8.0-nightly.124</c> on a nightly channel. This is a build's whole identity: the nightly tag rolls, the version does not repeat.</param>
/// <param name="Notes">The release notes packed into the feed — what changed.</param>
/// <param name="Url">The release page, for an operator who would rather read it in a browser.</param>
public sealed record AppRelease(string Version, string Notes, string Url);

/// <summary>What a check found: a build worth telling the operator about, or nothing.</summary>
/// <param name="Release">The newer build, or null when this cockpit is current.</param>
/// <param name="Failure">Why the check could not be made, or null. A check that failed is not an "up to date" — saying so would be a lie the operator would believe.</param>
public sealed record UpdateCheckResult(AppRelease? Release, string? Failure)
{
    public bool HasUpdate => Release is not null;

    public static UpdateCheckResult UpToDate => new(null, null);

    public static UpdateCheckResult Failed(string why) => new(null, why);
}
