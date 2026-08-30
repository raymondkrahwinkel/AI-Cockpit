using System.Diagnostics;
using System.Runtime.InteropServices;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Infrastructure.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

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
            terminate: jobName =>
            {
                terminated.Add(jobName);
                return true;
            });

        Assert.Empty(terminated);
        Assert.Equal(1, outcome.SkippedForPidReuse);
    }

    [Fact]
    public void Sweep_WhenThePreviousCockpitAndRootAreGone_TerminatesOnlyItsNamedJob()
    {
        var startedAt = new DateTimeOffset(2026, 8, 30, 10, 0, 0, TimeSpan.Zero);
        var record = new WindowsJobSessionRecord("cockpit-session-test", 100, startedAt, 200, startedAt);
        var terminated = new List<string>();

        var outcome = WindowsJobSessionSweep.Sweep(
            [record],
            processStartedAt: _ => null,
            terminate: jobName =>
            {
                terminated.Add(jobName);
                return true;
            });

        Assert.Equal([record.JobName], terminated);
        Assert.Equal(1, outcome.Terminated);
    }

    [Fact]
    public void OnWindows_DisposeTerminatesOnlyTheProcessTreeAddedToItsJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

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

    [Fact]
    public void OnWindows_SweepTerminatesAJobThisTestFilled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

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

    [Fact]
    public async Task SessionRuntime_DisposesItsProcessAnchorAfterTheDriver()
    {
        var driver = new _Driver();
        var anchor = new _Anchor();
        var runtime = new SessionRuntime(new _Factory(driver), profile: null, memoryLimiter: null, processAnchor: anchor);

        await runtime.StartAsync(profile: null);
        await runtime.DisposeAsync();

        Assert.Equal(4242, anchor.ProcessId);
        Assert.True(driver.Disposed);
        Assert.True(anchor.Disposed);
    }

    private sealed class _Anchor : ISessionProcessAnchor
    {
        public int? ProcessId { get; private set; }

        public bool Disposed { get; private set; }

        public IDisposable? Anchor(int processId)
        {
            ProcessId = processId;
            return new _Handle(this);
        }

        private sealed class _Handle(_Anchor anchor) : IDisposable
        {
            public void Dispose() => anchor.Disposed = true;
        }
    }

    private sealed class _Factory(ISessionDriver driver) : ISessionDriverFactory
    {
        public ISessionDriver Create(SessionProfile? profile) => driver;
    }

    private sealed class _Driver : ISessionDriver
    {
        public Task StreamFinished => Task.CompletedTask;

        public bool Disposed { get; private set; }

        public SessionCapabilities Capabilities => new(false, false, false, false, false, false, false, false);

        public int? ProcessId => 4242;

        public string? SessionId => null;

        public SessionProfile? Profile => null;

        public IAsyncEnumerable<SessionEvent> Events => AsyncEnumerable.Empty<SessionEvent>();

        public Task StartAsync(SessionProfile? profile = null, string? permissionMode = null, string? model = null, IReadOnlySet<string>? enabledMcpServerNames = null, string? workingDirectory = null, SessionResume? resume = null, IReadOnlyDictionary<string, string>? launchOptions = null, string? projectId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendUserMessageAsync(string text, IReadOnlyList<ImageAttachment>? images = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetModelAsync(string? model, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetMaxThinkingTokensAsync(int maxThinkingTokens, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task AllowPermissionAlwaysAsync(string toolUseId, string toolName, string proposedInputJson, PermissionRuleScope scope, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
