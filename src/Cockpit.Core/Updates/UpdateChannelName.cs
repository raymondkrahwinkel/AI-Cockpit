namespace Cockpit.Core.Updates;

// The name of the feed a build reads: `{platform}-{stream}` — `win-stable`, `linux-nightly` (AC-387).
// These are the names `vpk pack --channel` writes in the release and nightly workflows.
//
// The platform is part of the channel rather than a filter applied to it, because a filter is a step somebody can
// forget. All three platforms publish into one GitHub release, so a channel named only for the stream would let a
// Windows install be offered a macOS package — the failure this project is most exposed to, and one that happens on
// somebody else's machine rather than in CI.
public static class UpdateChannelName
{
    // The channel this build is allowed to read.
    public static string For(UpdateChannel stream) => For(Platform(), stream);

    // The name for a given platform. Split out so the rule can be asked for all three from one machine — the
    // alternative is a test that only ever proves the platform it happens to run on.
    internal static string For(string platform, UpdateChannel stream) =>
        $"{platform}-{(stream == UpdateChannel.Nightly ? "nightly" : "stable")}";

    // What this machine calls itself in a channel name. The cockpit ships for three RIDs and no others, so a fourth
    // platform is a build that was never published: refusing is honest, and the refusal surfaces as a failed check
    // rather than as a silent "you are up to date".
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
