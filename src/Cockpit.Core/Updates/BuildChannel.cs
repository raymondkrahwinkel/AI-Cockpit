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
    /// The stream a version belongs to. Only the nightly prerelease tag means nightly: a release candidate
    /// (<c>1.0.0-rc.1</c>) is published by the release workflow onto the stable channel, so it belongs there too.
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
