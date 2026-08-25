namespace Cockpit.Core.Updates;

// Which stream a build belongs to, read from the build itself (AC-387) — the channel a cockpit follows until an
// operator says otherwise. Defaulting to `UpdateChannel.Stable` instead would offer a nightly user a stable
// "update" that is really a downgrade; the build already knows what it is, so ask it.
public static class BuildChannel
{
    private const string Nightly = "nightly";

    // The stream a version belongs to. Only the nightly prerelease tag means nightly; every other prerelease reads
    // as stable, the answer that offers less. A release candidate can't arrive from the pipeline at all — the
    // workflow's tag gate accepts only `vX.Y.Z` and turns away tags like `v0.8.0-rc.1`.
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
