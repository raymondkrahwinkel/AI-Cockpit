using System.Diagnostics;
using System.Runtime.InteropServices;
using Cockpit.Infrastructure.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.Versioning;

namespace Cockpit.Infrastructure.Tests.Sessions;

public sealed class WindowsJobSessionAnchorTests
{

    [Fact]
    public void Sweep_WhenTheRecordedRootPidWasReused_LeavesTheJobAlone()
    {
        var startedAt = new DateTimeOffset(2026, 8, 30, 10, 0, 0, TimeSpan.Zero);
        var record = new WindowsJobSessionRecord("cockpit-session-test", 100, startedAt, 200, startedAt);
        var terminated = new List<string>();

        var outcome = WindowsJobSessionSweep.Sweep(
            [record],
            processStartedAt: processId => processId == 200 ? startedAt.AddMinutes(1) : null,
            terminate: target =>
            {
                terminated.Add(target.JobName);
                return WindowsJobSessionSweep.JobTermination.Terminated;
            });

        Assert.Empty(terminated);
        Assert.Equal(1, outcome.SkippedForPidReuse);
    }

    [Fact]
    public void Sweep_ForOnePane_StopsOnlyThatPanesOrphanAndNamesIt()
    {
        var startedAt = new DateTimeOffset(2026, 10, 9, 11, 25, 0, TimeSpan.Zero);
        var orphan = new WindowsJobSessionRecord("orphan", 100, startedAt, 200, startedAt, "pane-a");
        var otherPane = new WindowsJobSessionRecord("other", 100, startedAt, 300, startedAt, "pane-b");
        var live = new WindowsJobSessionRecord("live", 400, startedAt, 500, startedAt, "pane-a");
        var terminated = new List<string>();

        // AC-1519: stop_agent sweeps one pane, and only a tree whose Cockpit is gone counts as that pane's orphan.
        var outcome = WindowsJobSessionSweep.Sweep(
            [orphan, otherPane, live],
            processStartedAt: processId => processId == 400 ? startedAt : null,
            terminate: target =>
            {
                terminated.Add(target.JobName);
                return WindowsJobSessionSweep.JobTermination.Terminated;
            },
            paneId: "pane-a");

        Assert.Equal(["orphan"], terminated);
        Assert.Equal(["pane-a"], outcome.StoppedPanes);
        Assert.Equal(1, outcome.SkippedForLiveOwner);
    }

    [SupportedOSPlatform("windows")]
    [WindowsFact("Job objects are a Windows kernel feature.")]
    public void OnWindows_DisposeTerminatesOnlyTheProcessTreeAddedToItsJob()
    {
        var registryPath = Path.Combine(Path.GetTempPath(), $"session-jobs-{Guid.NewGuid():N}.json");
        using var child = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 60\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        try
        {
            using var anchor = new WindowsJobSessionAnchor(
                    new WindowsJobSessionRegistry(registryPath, NullLogger<WindowsJobSessionRegistry>.Instance),
                    NullLogger<WindowsJobSessionAnchor>.Instance)
                .Anchor(child.Id);

            Assert.NotNull(anchor);
            anchor.Dispose();
            Assert.True(child.WaitForExit(10_000), "The job did not stop its own test process.");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }

            if (File.Exists(registryPath))
            {
                File.Delete(registryPath);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    [WindowsFact("Job objects are a Windows kernel feature.")]
    public void OnWindows_SweepTerminatesAJobThisTestFilled()
    {
        var registryPath = Path.Combine(Path.GetTempPath(), $"session-jobs-{Guid.NewGuid():N}.json");
        using var child = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 60\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        try
        {
            var registry = new WindowsJobSessionRegistry(registryPath, NullLogger<WindowsJobSessionRegistry>.Instance);
            using var anchor = new WindowsJobSessionAnchor(registry, NullLogger<WindowsJobSessionAnchor>.Instance).Anchor(child.Id);
            var record = Assert.Single(registry.Load());
            registry.Remove(record.JobName);
            Assert.True(registry.TryRegister(record with { OwnerProcessId = -1 }));

            var job = WindowsJobSessionAnchor.NativeMethods.OpenJobObjectW(0x0008, inheritHandle: false, record.JobName);
            var error = Marshal.GetLastWin32Error();
            Assert.True(job != IntPtr.Zero, $"OpenJobObject failed ({error}).");
            WindowsJobSessionAnchor.NativeMethods.CloseHandle(job);

            var outcome = WindowsJobSessionSweep.Sweep(registry.Load(), WindowsJobSessionAnchor.StartedAt, WindowsJobSessionSweep.Terminate);

            Assert.True(
                outcome.Terminated == 1,
                $"terminated={outcome.Terminated}; liveOwners={outcome.SkippedForLiveOwner}; reusedPids={outcome.SkippedForPidReuse}");
            Assert.True(child.WaitForExit(10_000), "The sweep did not stop its own test job.");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }

            if (File.Exists(registryPath))
            {
                File.Delete(registryPath);
            }
        }
    }

}
