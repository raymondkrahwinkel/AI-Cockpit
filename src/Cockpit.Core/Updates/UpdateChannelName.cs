namespace Cockpit.Core.Updates;

/// <summary>
/// The name of the feed a build reads: <c>{platform}-{stream}</c> — <c>win-stable</c>, <c>linux-nightly</c> (AC-387).
/// These are the names <c>vpk pack --channel</c> writes in the release and nightly workflows.
/// <para>
/// The platform is part of the channel rather than a filter applied to it, because a filter is a step somebody can
/// forget. All three platforms publish into one GitHub release, so a channel named only for the stream would let a
/// Windows install be offered a macOS package — the failure this project is most exposed to, and one that happens on
/// somebody else's machine rather than in CI.
/// </para>
/// </summary>
public static class UpdateChannelName
{
    /// <summary>The channel this build is allowed to read.</summary>
    public static string For(UpdateChannel stream) => For(Platform(), stream);

    /// <summary>
    /// The name for a given platform. Split out so the rule can be asked for all three from one machine — the
    /// alternative is a test that only ever proves the platform it happens to run on.
    /// </summary>
    internal static string For(string platform, UpdateChannel stream) =>
        $"{platform}-{(stream == UpdateChannel.Nightly ? "nightly" : "stable")}";

    /// <summary>
    /// What this machine calls itself in a channel name. The cockpit ships for three RIDs and no others, so a fourth
    /// platform is a build that was never published: refusing is honest, and the refusal surfaces as a failed check
    /// rather than as a silent "you are up to date".
    /// </summary>
    internal static string Platform()
    {
        if (OperatingSystem.IsWindows())
        {
            return "win";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "osx";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        throw new PlatformNotSupportedException(
            "The cockpit is published for Windows, macOS and Linux; this platform has no update channel.");
    }
}
