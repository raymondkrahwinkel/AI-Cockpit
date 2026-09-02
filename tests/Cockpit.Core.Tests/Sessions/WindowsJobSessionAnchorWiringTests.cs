using System.Diagnostics;
using System.Runtime.InteropServices;
using Cockpit.App;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Infrastructure;
using Cockpit.Infrastructure.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Core.Tests.Sessions;

public sealed class WindowsJobSessionAnchorWiringTests
{
    [WindowsFact("Job objects are a Windows kernel feature; the anchor is a no-op elsewhere.")]
    public async Task OnWindows_AContainerCreatedSessionPlacesItsProcessInAJob()
    {
        var registryPath = Path.Combine(Path.GetTempPath(), $"session-jobs-{Guid.NewGuid():N}.json");
        var registry = new WindowsJobSessionRegistry(registryPath, NullLogger<WindowsJobSessionRegistry>.Instance);
        try
        {
            await _AssertContainerSessionIsAnchoredAsync(registry);
        }
        finally
        {
            if (File.Exists(registryPath))
            {
                File.Delete(registryPath);
            }
        }
    }

    private static async Task _AssertContainerSessionIsAnchoredAsync(WindowsJobSessionRegistry registry)
    {
        var driver = new _SleepingDriver();
        var services = _ProductionServices();
        services.AddSingleton(registry);
        services.AddSingleton<ISessionDriverFactory>(new _DriverFactory(driver));

        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<ISessionManager>();
        var runtime = manager.Create(profile: null);

        try
        {
            await runtime.StartAsync(profile: null);
            var processId = Assert.IsType<int>(runtime.ProcessId);
            using var process = Process.GetProcessById(processId);
            var record = Assert.Single(registry.Load());
            var job = NativeMethods.OpenJobObjectW(NativeMethods.JobObjectQuery, inheritHandle: false, record.JobName);
            var error = Marshal.GetLastWin32Error();
            Assert.True(job != IntPtr.Zero, $"OpenJobObject failed ({error}).");

            try
            {
                Assert.True(
                    NativeMethods.IsProcessInJob(process.Handle, job, out var inJob),
                    $"IsProcessInJob failed ({Marshal.GetLastWin32Error()}).");
                Assert.True(inJob, "The process created through the production container was not assigned to its recorded Windows job.");
            }
            finally
            {
                NativeMethods.CloseHandle(job);
            }
        }
        finally
        {
            await manager.StopAsync(runtime.Id);
        }
    }

    private static Process _StartSleepingProcess() => Process.Start(new ProcessStartInfo
    {
        FileName = "powershell.exe",
        Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 60\"",
        UseShellExecute = false,
        CreateNoWindow = true,
    })!;

    private static ServiceCollection _ProductionServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly,
            typeof(CockpitViewModel).Assembly);
        services.AddSessionPanes();
        return services;
    }

    private sealed class _DriverFactory(ISessionDriver driver) : ISessionDriverFactory
    {
        public ISessionDriver Create(SessionProfile? profile) => driver;
    }

    private sealed class _SleepingDriver : ISessionDriver
    {
        private Process? _process;

        public Task StreamFinished => Task.CompletedTask;

        public SessionCapabilities Capabilities => new(false, false, false, false, false, false, false, false);

        public int? ProcessId => _process?.Id;

        public string? SessionId => null;

        public SessionProfile? Profile => null;

        public IAsyncEnumerable<SessionEvent> Events => AsyncEnumerable.Empty<SessionEvent>();

        public Task StartAsync(SessionProfile? profile = null, string? permissionMode = null, string? model = null, IReadOnlySet<string>? enabledMcpServerNames = null, string? workingDirectory = null, SessionResume? resume = null, IReadOnlyDictionary<string, string>? launchOptions = null, string? projectId = null, CancellationToken cancellationToken = default)
        {
            _process = _StartSleepingProcess();
            return Task.CompletedTask;
        }

        public Task SendUserMessageAsync(string text, IReadOnlyList<ImageAttachment>? images = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetModelAsync(string? model, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetMaxThinkingTokensAsync(int maxThinkingTokens, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task AllowPermissionAlwaysAsync(string toolUseId, string toolName, string proposedInputJson, PermissionRuleScope scope, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }

            _process?.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static class NativeMethods
    {
        public const uint JobObjectQuery = 0x0004;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenJobObjectW(uint desiredAccess, bool inheritHandle, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
