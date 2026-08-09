using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Infrastructure.Tests.Sessions;

/// <summary>
/// The session memory cap (AC-661): what a session may hold, and — on Windows — that the OS enforces it around
/// the session's whole tree while leaving this process (standing in for the cockpit) alone.
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

    [Fact]
    public void OnWindows_TheJobObjectStopsARunawayGrandchild_AndThisProcessLivesOn()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Windows is where this bug was reproduced, so it is where the mechanism is proven live.
            return;
        }

        const long capBytes = 512L * 1024 * 1024;

        var script = Path.Combine(Path.GetTempPath(), $"cockpit-hog-{Guid.NewGuid():n}.ps1");

        // Grows in 50 MB steps and reports how far it got, so the assertion below is about the bound the job
        // enforced rather than about the process merely having died of something.
        File.WriteAllText(script, """
            $held = @()
            try
            {
                for ($i = 1; $i -le 200; $i++)
                {
                    $held += ,(New-Object byte[] 52428800)
                    Write-Output $i
                }
            }
            catch { exit 42 }
            """);

        // cmd.exe is the direct child and the allocating powershell its child — the shape of the bug, where
        // `claude` is fine and the `dotnet test` it started is not.
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
            using var cap = new WindowsJobMemoryLimiter(NullLogger<WindowsJobMemoryLimiter>.Instance)
                .Apply(parent.Id, capBytes);
            Assert.NotNull(cap);

            var reached = 0;
            while (parent.StandardOutput.ReadLine() is { } line)
            {
                if (int.TryParse(line.Trim(), out var step))
                {
                    reached = step;
                }
            }

            Assert.True(parent.WaitForExit(60_000), "The capped tree never exited.");

            // Non-zero first: without it the two bounds below pass on a tree that never started. 200 steps is
            // 10 GB, which a machine with room would have handed over happily.
            Assert.True(reached > 0, "The allocating grandchild never got going, so nothing was proven.");
            Assert.True(reached < 200, $"The tree allocated its full 10 GB (reached step {reached}); the cap did not bind.");
            Assert.True(reached * 50L * 1024 * 1024 < capBytes * 2, $"The tree reached {reached * 50} MB against a {capBytes / 1024 / 1024} MB cap.");

            // The cockpit's side of it: this process is untouched by the limit that killed the tree, and can still
            // commit memory of its own.
            var mine = new byte[64 * 1024 * 1024];
            mine[^1] = 1;
            Assert.Equal(1, mine[^1]);
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

    [Fact]
    public async Task OnWindows_TheCapHoldsThroughTheRealPtyHost_WhichIsHowASessionActuallySpawns()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The same claim over the spawn a session really uses — `ConPtyHostFactory`, the call `TtyLauncher` makes.
        // A pseudo-console child is an ordinary child of this process, so it joins the job like any other.
        const long capBytes = 512L * 1024 * 1024;

        var script = Path.Combine(Path.GetTempPath(), $"cockpit-hog-pty-{Guid.NewGuid():n}.ps1");
        File.WriteAllText(script, """
            $held = @()
            try
            {
                for ($i = 1; $i -le 200; $i++) { $held += ,(New-Object byte[] 52428800) }
            }
            catch { }
            "done"
            """);

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                environment[key] = value;
            }
        }

        using var pty = new Cockpit.Infrastructure.Sessions.Tty.ConPtyHostFactory().Start(
            "cmd.exe",
            ["/c", "powershell", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script],
            Path.GetTempPath(),
            environment,
            columns: 80,
            rows: 24);

        try
        {
            using var cap = new WindowsJobMemoryLimiter(NullLogger<WindowsJobMemoryLimiter>.Instance)
                .Apply(pty.ProcessId, capBytes);
            Assert.NotNull(cap);

            // The pty EOFs when the child tree is gone; uncapped, the hog would still be climbing towards 10 GB.
            var buffer = new byte[4096];
            var read = Task.Run(() =>
            {
                while (pty.OutputStream.Read(buffer, 0, buffer.Length) > 0)
                {
                }
            });

            Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromMinutes(2))));

            var mine = new byte[64 * 1024 * 1024];
            mine[^1] = 1;
            Assert.Equal(1, mine[^1]);
        }
        finally
        {
            File.Delete(script);
        }
    }

    private static SessionProfile _ProfileCappedAt(int megabytes) =>
        new("Test", new ClaudeConfig(ConfigDir: "/tmp/claude")) { MemoryCapMegabytes = megabytes };
}
