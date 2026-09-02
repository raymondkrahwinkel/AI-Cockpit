using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Infrastructure.Shell;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Infrastructure.Tests.Shell;

public class TrackedCommandRunnerTests : IDisposable
{
    private readonly DirectoryInfo _workingDirectory = Directory.CreateTempSubdirectory("cockpit-tracked-runner-test-");
    private readonly TrackedCommandRunner _runner = new(NullLogger<TrackedCommandRunner>.Instance);

    public void Dispose() => _workingDirectory.Delete(recursive: true);

    private Task<TrackedRunResult> _RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout) =>
        _runner.RunAsync(_workingDirectory.FullName, command, arguments, timeout, Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RunAsync_RunsTheCommand_AndReportsExitCodeAndStdout()
    {
        var result = await _RunAsync("dotnet", ["--version"], TimeSpan.FromSeconds(30));

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.NotEmpty(result.StandardOutput);
    }

    [PosixFact("Not yet covered on Windows rather than inapplicable: there the metacharacter would be `&`, not `;`, and nothing echoes argv verbatim without a shell to prove it against; the ArgumentList path itself is covered there by RunAsync_RunsTheCommand_AndReportsExitCodeAndStdout.")]
    public async Task RunAsync_NeverLetsAShellReparseAnArgument()
    {
        var result = await _RunAsync("echo", ["a; rm -rf poisoned"], TimeSpan.FromSeconds(30));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("a; rm -rf poisoned", result.StandardOutput);
        Assert.False(Directory.Exists(Path.Combine(_workingDirectory.FullName, "poisoned")));
    }

    [Fact]
    public async Task RunAsync_PastTheTimeout_EndsTheProcess_AndReportsTimedOut()
    {
        var (command, arguments) = PlatformCommands.RunsForThirtySeconds();

        var result = await _RunAsync(command, arguments, TimeSpan.FromMilliseconds(300));

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_ALargeOutput_DoesNotDeadlock()
    {
        var (command, arguments) = PlatformCommands.WritesToStandardOutput(200000);

        var result = await _RunAsync(command, arguments, TimeSpan.FromSeconds(30));

        Assert.False(result.TimedOut);
        Assert.Equal(200000, result.StandardOutput.Length);
    }

    /// <summary>
    /// AC-1094 criterion 8, against a real cgroup v2 — the property no fake can stand in for: a process that
    /// double-forks out of the run's tree and is reparented to pid 1 before the run ends. A ppid walk
    /// (<c>Process.Kill(entireProcessTree: true)</c>) never sees it again once that happens; this must end it
    /// anyway, because cgroup membership does not change on reparenting. Skips rather than fails where there is no
    /// real cgroup v2 to prove it against (non-Linux, no delegation) — same as `LinuxCrashLogReaderTests`.
    /// </summary>
    [LinuxFact("Cgroup v2 membership is a Linux kernel feature; there is nothing to prove elsewhere.")]
    public async Task RunAsync_EndsAGrandchildReparentedToPidOne_ThatAProcessTreeWalkWouldMiss()
    {
        // The platform half is the attribute above. This is the runtime half: a Linux box without cgroup
        // delegation has no real cgroup v2 to prove this against. xunit 2.9 has no dynamic skip, so it stays a
        // guard — the one case here that still reports a pass it did not earn, and only on such a box.
        if (LinuxCgroupMemoryLimiter.FindWritableParent() is null)
        {
            return;
        }

        var marker = Path.Combine(_workingDirectory.FullName, "orphan-pid");

        // The subshell backgrounds a `setsid` grandchild and exits immediately, which reparents it to pid 1 within
        // the same instant — exactly the shape a build server left behind by its own parent takes. The outer
        // process then sits in its own sleep, long enough for the timeout below to still find it running, the same
        // way a real `dotnet test` run is still going when an MSBuild node it already spawned outlives it.
        var script = $"(setsid sleep 30 </dev/null >/dev/null 2>&1 & echo $! > '{marker}'); sleep 30";

        var result = await _RunAsync("sh", ["-c", script], TimeSpan.FromMilliseconds(500));

        Assert.True(result.TimedOut);

        var orphanPid = int.Parse((await File.ReadAllTextAsync(marker)).Trim());
        Assert.False(Directory.Exists($"/proc/{orphanPid}"), "the reparented grandchild must not survive cleanup");
    }

    /// <summary>
    /// AC-1094 criterion 9 — the destructive half is asymmetric: a cleanup that reaches too far is noticed once,
    /// never twice. Proves the run's cgroup ends only what it holds: a second, unrelated process started outside
    /// this run survives the same cleanup that ends the run's own reparented grandchild.
    /// </summary>
    [LinuxFact("Cgroup v2 membership is a Linux kernel feature; there is nothing to prove elsewhere.")]
    public async Task RunAsync_LeavesAnUnrelatedProcessRunning()
    {
        // The platform half is the attribute above. This is the runtime half: a Linux box without cgroup
        // delegation has no real cgroup v2 to prove this against. xunit 2.9 has no dynamic skip, so it stays a
        // guard — the one case here that still reports a pass it did not earn, and only on such a box.
        if (LinuxCgroupMemoryLimiter.FindWritableParent() is null)
        {
            return;
        }

        using var bystander = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sleep", "30")
        {
            UseShellExecute = false,
        })!;

        try
        {
            var result = await _RunAsync("sleep", ["30"], TimeSpan.FromMilliseconds(300));

            Assert.True(result.TimedOut);
            Assert.False(bystander.HasExited, "cleanup for one run must not touch a process it never started");
        }
        finally
        {
            if (!bystander.HasExited)
            {
                bystander.Kill();
            }
        }
    }
}
