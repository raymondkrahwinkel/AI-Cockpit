using Cockpit.Core.Diagnostics;

namespace Cockpit.Core.Tests.Diagnostics;

/// <summary>
/// Tests <see cref="AppImageMount"/> — the probe that tells a live AppImage mount from one whose daemon has
/// gone, which is the difference between a running cockpit and the SIGBUS coredumps of AC-1114.
/// </summary>
public class AppImageMountTests
{
    [Fact]
    public void APathThatStatsButCannotBeOpenedCountsAsNotServing()
    {
        if (OperatingSystem.IsWindows())
        {
            // Mode bits are the only portable way to make an existing file refuse to open, and AppImages —
            // the thing being probed — are Linux-only anyway.
            return;
        }

        var served = Path.Combine(Path.GetTempPath(), $"ac1114-live-{Guid.NewGuid():N}");
        var refused = Path.Combine(Path.GetTempPath(), $"ac1114-dead-{Guid.NewGuid():N}");
        File.WriteAllText(served, "AppRun");
        File.WriteAllText(refused, "AppRun");

        // A dead mount goes on answering metadata and refuses every open, so the probe has to actually open.
        // Clearing the mode bits reproduces that shape; any File.Exists-shaped check would call this healthy.
        File.SetUnixFileMode(refused, UnixFileMode.None);

        try
        {
            Assert.True(AppImageMount.CanStillServe(served));

            Assert.True(File.Exists(refused));
            Assert.False(AppImageMount.CanStillServe(refused));
        }
        finally
        {
            File.SetUnixFileMode(refused, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Delete(refused);
            File.Delete(served);
        }
    }

    [Fact]
    public void AnAppDirWhoseProbeNeverReadIsNotWatchedAtAll()
    {
        var appDir = Directory.CreateTempSubdirectory("ac1114-appdir-").FullName;

        try
        {
            // A dev shell sets APPDIR too, and an unpacked layout may name its AppRun differently. Watching
            // that would report a mount as lost twenty seconds in while nothing was ever there to lose.
            Assert.Null(AppImageMount.WatchablePathFrom(appDir));

            File.WriteAllText(Path.Combine(appDir, "AppRun"), "#!/bin/sh");
            Assert.NotNull(AppImageMount.WatchablePathFrom(appDir));
        }
        finally
        {
            Directory.Delete(appDir, recursive: true);
        }
    }
}
