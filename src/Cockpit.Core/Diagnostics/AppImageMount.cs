namespace Cockpit.Core.Diagnostics;

// AC-1114: an AppImage runs its own code from a squashfuse mount. When that mount's daemon disappears the
// mount stays listed but stops serving, and every code page not yet resident then faults with SIGBUS — four
// coredumps on 2026-08-26 were exactly that. This is the probe that spots the condition.
public static class AppImageMount
{
    // AppRun sits in the AppDir root of every AppImage this project builds — see scripts/package-appimage.sh.
    private const string ProbeFileName = "AppRun";

    // Only a mount that reads at startup is worth watching. APPDIR is also set by a dev shell, and by
    // unpacked layouts whose AppRun is named differently — "could never read this" must not later come out
    // as "the mount died". Null means there is nothing to watch.
    public static string? WatchablePathFrom(string? appDir)
    {
        if (string.IsNullOrWhiteSpace(appDir))
        {
            return null;
        }

        var probePath = Path.Combine(appDir, ProbeFileName);
        return CanStillServe(probePath) ? probePath : null;
    }

    // Opens the file, because opening is the only thing a dead mount actually refuses: metadata keeps
    // answering on one, so File.Exists and any stat-shaped check both succeed while every open fails with
    // ENOTCONN. Measured on three dead mounts on 2026-08-27.
    public static bool CanStillServe(string probePath)
    {
        try
        {
            using var stream = File.OpenRead(probePath);

            // An empty probe file would still mean the mount serves, so the byte is read but not judged.
            stream.ReadByte();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
