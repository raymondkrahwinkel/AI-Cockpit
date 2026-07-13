namespace Cockpit.Core.Updates;

/// <summary>Which builds this cockpit is willing to be told about (#71).</summary>
public enum UpdateChannel
{
    /// <summary>Tagged releases only — <c>v1.2.3</c>. The default: a cockpit that quietly moves onto last night's build is not one you can trust with a day's work.</summary>
    Stable,

    /// <summary>Also the rolling nightly build of main. Opt-in, and it means what it says: main, as it was last night.</summary>
    Nightly,
}

/// <summary>
/// A release as GitHub has it (#71).
/// </summary>
/// <param name="Version">The semver of a tagged release (<c>1.2.3</c>), or empty for the nightly, which has none — that is what a rolling tag means.</param>
/// <param name="Commit">The commit it was built from. For a nightly this is its whole identity: there is no number to compare, so the question "is this a different build than mine" is answered by the sha and nothing else.</param>
/// <param name="Name">What the release calls itself, for the operator to read.</param>
/// <param name="Notes">The release body — what changed.</param>
/// <param name="Url">Where to get it.</param>
/// <param name="PublishedAt">When it was published.</param>
/// <param name="IsPrerelease">True for the nightly.</param>
public sealed record AppRelease(
    string Version,
    string Commit,
    string Name,
    string Notes,
    string Url,
    DateTimeOffset PublishedAt,
    bool IsPrerelease);

/// <summary>What a check found: a build worth telling the operator about, or nothing.</summary>
/// <param name="Release">The newer build, or null when this cockpit is current.</param>
/// <param name="Failure">Why the check could not be made, or null. A check that failed is not an "up to date" — saying so would be a lie the operator would believe.</param>
public sealed record UpdateCheckResult(AppRelease? Release, string? Failure)
{
    public bool HasUpdate => Release is not null;

    public static UpdateCheckResult UpToDate => new(null, null);

    public static UpdateCheckResult Failed(string why) => new(null, why);
}
