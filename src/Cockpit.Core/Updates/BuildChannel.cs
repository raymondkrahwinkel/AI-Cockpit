namespace Cockpit.Core.Updates;

/// <summary>
/// Which stream a build belongs to, read from the build itself (AC-387). This is the channel a cockpit follows until
/// an operator says otherwise.
/// <para>
/// The alternative — defaulting to <see cref="UpdateChannel.Stable"/> — is how somebody who deliberately downloaded a
/// nightly and started it without a configuration file gets offered the latest stable as their next "update", which
/// is a downgrade. The build already knows what it is; asking it is cheaper than asking the operator to repair a
/// default they never chose.
/// </para>
/// </summary>
public static class BuildChannel
{
    private const string Nightly = "nightly";

    /// <summary>
    /// The stream a version belongs to. Only the nightly prerelease tag means nightly; every other prerelease reads
    /// as stable, which is the answer that offers less. A release candidate is the case to think about, and it cannot
    /// arrive from the pipeline at all — the release workflow's tag gate accepts <c>vX.Y.Z</c> and nothing else, and
    /// names <c>v0.8.0-rc.1</c> as one of the tags it exists to turn away. So a version like that is a build made some
    /// other way, and guessing it wants nightlies would be a guess with a downgrade on the other side of it.
    /// </summary>
    public static UpdateChannel FromVersion(string version)
    {
        var text = version.Trim();

        // The assembly's informational version carries "+<sha>" build metadata, which is not part of the tag.
        var build = text.IndexOf('+');
        if (build >= 0)
        {
            text = text[..build];
        }

        var dash = text.IndexOf('-');

        return dash >= 0 && text[(dash + 1)..].StartsWith(Nightly, StringComparison.OrdinalIgnoreCase)
            ? UpdateChannel.Nightly
            : UpdateChannel.Stable;
    }
}
