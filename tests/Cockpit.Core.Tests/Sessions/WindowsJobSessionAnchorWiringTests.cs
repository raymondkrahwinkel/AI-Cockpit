using System.Diagnostics;
using System.Runtime.InteropServices;
using Cockpit.App;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Core.Tests.Sessions;

public sealed class WindowsJobSessionAnchorWiringTests
{
    [Fact]
    public async Task OnWindows_AContainerCreatedSessionPlacesItsProcessInAJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var driver = new _SleepingDriver();
        var services = _ProductionServices();
        services.AddSingleton<ISessionDriverFactory>(new _DriverFactory(driver));

        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<ISessionManager>();
        var runtime = manager.Create(profile: null);

        try
        {
            await runtime.StartAsync(profile: null);
            var processId = Assert.IsType<int>(runtime.ProcessId);
            using var process = Process.GetProcessById(processId);

            Assert.True(
                NativeMethods.IsProcessInJob(process.Handle, IntPtr.Zero, out var inJob),
                $"IsProcessInJob failed ({Marshal.GetLastWin32Error()}).");
            Assert.True(inJob, "The process created through the production container was not assigned to a Windows job.");
        }
        finally
        {
            await manager.StopAsync(runtime.Id);
        }
    }

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
            _process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 60\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
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
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    }
}
