namespace Cockpit.Core.Updates;

// Which stream a build belongs to, read from the build itself (AC-387). This is the channel a cockpit follows until
// an operator says otherwise.
//
// The alternative — defaulting to `UpdateChannel.Stable` — is how somebody who deliberately downloaded a
// nightly and started it without a configuration file gets offered the latest stable as their next "update", which
// is a downgrade. The build already knows what it is; asking it is cheaper than asking the operator to repair a
// default they never chose.
public static class BuildChannel
{
    private const string Nightly = "nightly";

    // The stream a version belongs to. Only the nightly prerelease tag means nightly; every other prerelease reads
    // as stable, which is the answer that offers less. A release candidate is the case to think about, and it cannot
    // arrive from the pipeline at all — the release workflow's tag gate accepts `vX.Y.Z` and nothing else, and
    // names `v0.8.0-rc.1` as one of the tags it exists to turn away. So a version like that is a build made some
    // other way, and guessing it wants nightlies would be a guess with a downgrade on the other side of it.
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
