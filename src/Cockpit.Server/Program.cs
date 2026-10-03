using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Hosting;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Server;

// AC-1444: the backend as a process of its own. The desktop's `Program.Main` steps in their order, the unlock from a
// file rather than a window, connect keys as the only way in, and a stop on SIGTERM. ponytail: no generic host, the
// listeners already run in the node-endpoint host; `IHost` once a second hosted lifecycle joins.
internal static class Program
{
    // Within the ten seconds a `docker stop` waits before it kills.
    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(8);

    public static async Task<int> Main()
    {
        // First, so a signal during the start is held until the start is done rather than ending the process mid-way.
        var stopRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => _RequestStop(context, stopRequested));
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => _RequestStop(context, stopRequested));

        CockpitBackend.ScrubInheritedEnvironment();
        CockpitBackend.MarkProcess();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        }));
        var logger = loggerFactory.CreateLogger("Cockpit.Server");
        logger.LogInformation("Cockpit.Server {Version} starting: pid {ProcessId}.", HostVersionInfo.Current, Environment.ProcessId);
        CockpitBackend.RepairProcess(loggerFactory);

        var backend = CockpitBackend.Build(loggerFactory, frontend: null, PluginStartup.Load);

        // The unlock needs the container's protection service, so it follows Build; nothing has started yet.
        var unlock = await UnlockFromFile.RunAsync(backend.Services.GetRequiredService<ISecretProtectionService>(), logger);
        if (unlock.Result == UnlockFromFileResult.Refused)
        {
            logger.LogError("Not starting: {Reason}", unlock.Reason);
            await backend.Services.DisposeAsync();
            return 1;
        }

        // AC-1355: connect keys only, so no LAN discovery and no pairing.
        backend.Services.GetRequiredService<NodeLanOnboarding>().Enabled = false;
        backend.Start();
        backend.SeedPluginSettings();
        backend.InitializePlugins();
        backend.StartPlanners();

        var uiAssemblies = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name ?? string.Empty).Where(_IsUi).ToList();
        logger.LogInformation(
            "Cockpit.Server running; UI assemblies loaded: {UiAssemblies}.", uiAssemblies.Count == 0 ? "none" : string.Join(", ", uiAssemblies));

        await stopRequested.Task;
        logger.LogInformation("Stop requested; stopping within {Budget}.", StopBudget);
        await backend.StopAsync(StopBudget);
        logger.LogInformation("Cockpit.Server stopped.");
        return 0;
    }

    // Cancelled, so the runtime does not end the process before the stop has run.
    private static void _RequestStop(PosixSignalContext context, TaskCompletionSource stopRequested)
    {
        context.Cancel = true;
        stopRequested.TrySetResult();
    }

    private static bool _IsUi(string name) =>
        name.StartsWith("Avalonia", StringComparison.Ordinal)
        || name == "Cockpit.App"
        || (name.StartsWith("Cockpit.", StringComparison.Ordinal) && name.EndsWith(".UI", StringComparison.Ordinal));
}
