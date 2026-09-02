using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Diagnostics;
using Cockpit.Infrastructure.Sessions;
using System.Runtime.Versioning;

namespace Cockpit.Infrastructure.Tests.Sessions;

/// <summary>
/// The session memory cap (AC-661): what a session may hold. AC-692 retired the Windows-side enforcement this
/// class used to prove live (a Job Object that killed the tree over its cap) — the live test below now proves the
/// opposite on the same real hardware: a tree well past its cap keeps running, because nothing here stops it.
/// </summary>
public class SessionMemoryCapTests
{
    [Fact]
    public void WithNothingConfigured_TheDefaultStands()
    {
        Assert.Equal(SessionMemoryCap.DefaultMegabytes, SessionMemoryCap.Megabytes(profile: null, launchOptions: null));
        Assert.Equal(SessionMemoryCap.DefaultMegabytes * 1024L * 1024L, SessionMemoryCap.ResolveBytes(profile: null, launchOptions: null));
    }

    [Fact]
    public void TheProfilesCapIsUsed_AndOneSpawnMayOverrideIt()
    {
        var profile = _ProfileCappedAt(2048);

        Assert.Equal(2048, SessionMemoryCap.Megabytes(profile, launchOptions: null));

        // The override is per launch, the same door AC-648 opens for model and effort — the profile keeps its own.
        Assert.Equal(
            16384,
            SessionMemoryCap.Megabytes(profile, new Dictionary<string, string> { [SessionMemoryCap.OptionKey] = "16384" }));
        Assert.Equal(2048, profile.MemoryCapMegabytes);
    }

    [Fact]
    public void ACapBelowTheFloorIsRaisedRatherThanObeyed()
    {
        // Under the floor the cap kills the agent CLI itself rather than a runaway build, which reads as a broken
        // cockpit. A session that starts with a cap it can live with beats one that does not start.
        Assert.Equal(SessionMemoryCap.MinimumMegabytes, SessionMemoryCap.Megabytes(_ProfileCappedAt(1), launchOptions: null));
    }

    [Fact]
    public void AnUnreadableCapIsRefused_NotSilentlyReadAsNoCap()
    {
        Assert.Null(SessionMemoryCap.RefusalFor("4096"));
        Assert.NotNull(SessionMemoryCap.RefusalFor("4 GB"));
        Assert.NotNull(SessionMemoryCap.RefusalFor("0"));
        Assert.NotNull(SessionMemoryCap.RefusalFor("-1"));

        // A garbage value never becomes "uncapped" by the back door either: resolution falls back to the default.
        Assert.Equal(
            SessionMemoryCap.DefaultMegabytes,
            SessionMemoryCap.Megabytes(profile: null, new Dictionary<string, string> { [SessionMemoryCap.OptionKey] = "lots" }));
    }

    [SupportedOSPlatform("windows")]
    [WindowsFact("Windows is where the original bug, and the no-kill contract that replaced its job-object fix, are proven live.")]
    public void OnWindows_ARunawayGrandchildIsNeverStopped_OnlyWatched()
    {
        // AC-692: WindowsJobMemoryLimiter's hard job-object kill (AC-661) is gone; Windows now shares
        // `PollingMemoryLimiter` with macOS. Scaled down from the old test's 10 GB ceiling — this machine runs
        // other agents' work at the same time.
        const long capBytes = 64L * 1024 * 1024;

        var script = Path.Combine(Path.GetTempPath(), $"cockpit-hog-{Guid.NewGuid():n}.ps1");

        // 16 steps of 10 MB is 160 MB total, 2.5x the cap — enough to prove the point without asking much of a
        // shared machine. Reports how far it got, so the assertion is about every step having run, not just that
        // the process eventually exited.
        File.WriteAllText(script, """
            $held = @()
            for ($i = 1; $i -le 16; $i++)
            {
                $held += ,(New-Object byte[] 10485760)
                Write-Output $i
                Start-Sleep -Milliseconds 200
            }
            """);

        // cmd.exe is the direct child and the allocating powershell its child — the shape of the bug this whole
        // mechanism exists for, where `claude` is fine and the `dotnet test` it started is not.
        using var parent = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        try
        {
            var limiter = new PollingMemoryLimiter(new WmiProcessTableReader(), NullLogger<PollingMemoryLimiter>.Instance);
            using var watch = limiter.Apply(parent.Id, capBytes);

            var reached = 0;
            while (parent.StandardOutput.ReadLine() is { } line)
            {
                if (int.TryParse(line.Trim(), out var step))
                {
                    reached = step;
                }
            }

            Assert.True(parent.WaitForExit(30_000), "The tree never exited on its own.");

            // The whole point: every step ran, well past the 64 MB cap, because nothing here stops it anymore.
            Assert.Equal(16, reached);
        }
        finally
        {
            if (!parent.HasExited)
            {
                parent.Kill(entireProcessTree: true);
            }

            File.Delete(script);
        }
    }

    private static SessionProfile _ProfileCappedAt(int megabytes) =>
        new("Test", new ClaudeConfig(ConfigDir: "/tmp/claude")) { MemoryCapMegabytes = megabytes };
}
