using System.Runtime.InteropServices;

namespace Cockpit.Core.Updates;

// Update feeds use `{platform}-{stream}` names that `vpk pack --channel` writes (AC-387).
// Platform belongs in the channel, not a forgettable filter: one release otherwise risks offering another OS's package.
// Encoding it at publication makes that mistake impossible instead of relying on every future reader to filter correctly.
public static class UpdateChannelName
{
    // The channel this build is allowed to read.
    public static string For(UpdateChannel stream) => For(Platform(), RuntimeInformation.ProcessArchitecture, stream);

    // Process architecture keeps an x64 installation under Rosetta on its own feed; ARM retains its existing names.
    internal static string For(string platform, Architecture architecture, UpdateChannel stream) =>
        $"{(platform == "osx" && architecture == Architecture.X64 ? "osx-x64" : platform)}-{(stream == UpdateChannel.Nightly ? "nightly" : "stable")}";

    // What this machine calls itself in a channel name. The cockpit ships for three OSes and no others, so a fourth
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
