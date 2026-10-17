using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Configuration;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Hosting;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Provisioning;

namespace Cockpit.Server;

// AC-1444: the backend as a process of its own. The desktop's `Program.Main` steps in their order, connect keys as the
// only way in, and a stop on SIGTERM. ponytail: no generic host, the
// listeners already run in the node-endpoint host; `IHost` once a second hosted lifecycle joins.
internal static class Program
{
    // The container's grace (compose `stop_grace_period`) sets it; the default stays within a `docker stop`'s ten seconds.
    private const string StopBudgetVariable = "COCKPIT_STOP_BUDGET_SECONDS";

    private const string UnlockPasswordFileVariable = "COCKPIT_UNLOCK_PASSWORD_FILE";

    // AC-1505: sysexits' EX_CONFIG, so a supervisor can tell "the provisioning source failed" from any other stop.
    private const int ProvisioningFailedExitCode = 78;

    private static readonly TimeSpan DefaultStopBudget = TimeSpan.FromSeconds(8);

    public static async Task<int> Main(string[] args)
    {
        // First, so a signal during the start is held until the start is done rather than ending the process mid-way.
        var stopRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => _RequestStop(context, stopRequested));
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => _RequestStop(context, stopRequested));

        // AC-1505: read before the scrub takes it out of the environment, so no session inherits the URL or its token.
        string? provisionUrl;
        try
        {
            provisionUrl = ProvisioningEntry.UrlFromEnvironment();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync($"Not starting: {ProvisioningEntry.UrlFileVariable} could not be read: {exception.Message}");
            return ProvisioningFailedExitCode;
        }

        CockpitBackend.ScrubInheritedEnvironment();
        CockpitBackend.MarkProcess();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        }));
        var logger = loggerFactory.CreateLogger("Cockpit.Server");
        logger.LogInformation("Cockpit.Server {Version} starting: pid {ProcessId}.", HostVersionInfo.Current, Environment.ProcessId);
        var stopBudget = _StopBudget(logger);
        logger.LogInformation("Stop budget: {Budget}.", stopBudget);
        CockpitBackend.RepairProcess(loggerFactory);

        // Route D, before any plugin loads: read and apply, all or nothing up to the write of cockpit.json (AC-1506).
        var provisioning = provisionUrl is null
            ? null
            : await ProvisioningEntry.ProvisionAsync(provisionUrl, args.Contains(ProvisioningEntry.ReapplyArgument), CockpitBuild.StateRoot, logger);
        if (provisioning is { Failure: not null })
        {
            logger.LogError("Not starting: provisioning failed (exit code {ExitCode}).", ProvisioningFailedExitCode);
            return ProvisioningFailedExitCode;
        }

        var backend = CockpitBackend.Build(loggerFactory, frontend: null, PluginStartup.Load);

        var protection = backend.Services.GetRequiredService<ISecretProtectionService>();
        if ((await protection.GetStatusAsync()).Enabled)
        {
            logger.LogError("Not starting: this state has encrypted credentials, and the server does not support encryption. Turn encryption off on the desktop (Options › Security) before copying the state, or start from an empty /state.");
            await backend.Services.DisposeAsync();
            return 1;
        }
        if (Environment.GetEnvironmentVariable(UnlockPasswordFileVariable) is not null)
        {
            logger.LogWarning("{Variable} is ignored because the server does not support encryption.", UnlockPasswordFileVariable);
        }

        // AC-1355: connect keys only, so no LAN discovery and no pairing. AC-1356: the door itself is on.
        backend.Services.GetRequiredService<NodeLanOnboarding>().Enabled = false;
        await NodeDoor.EnableAsync(backend.Services);
        await WorkRoots.ApplyAsync(backend.Services, logger);
        backend.Start();
        if (await NodeDoor.ProbeAsync(backend.Services) is { } notListening)
        {
            logger.LogError("Not starting: the node door is not listening: {Reason}", notListening);
            await backend.StopAsync(stopBudget);
            return 1;
        }

        backend.SeedPluginSettings();
        backend.InitializePlugins();
        if (provisioning is not null)
        {
            await ProvisionManifestApplier.FinishAsync(provisioning, backend.Services.GetRequiredService<PluginChannelHub>(), CockpitBuild.StateRoot, logger);
        }

        backend.StartPlanners();

        var uiAssemblies = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name ?? string.Empty).Where(_IsUi).ToList();
        logger.LogInformation(
            "Cockpit.Server running; UI assemblies loaded: {UiAssemblies}.", uiAssemblies.Count == 0 ? "none" : string.Join(", ", uiAssemblies));

        // AC-1525: an admin's restart takes the same stop; the supervisor brings the server back.
        var restart = backend.Services.GetRequiredService<ServerRestartSignal>().Requested;
        await Task.WhenAny(stopRequested.Task, restart);
        logger.LogInformation("{Request} requested; stopping within {Budget}.", restart.IsCompleted ? "Restart" : "Stop", stopBudget);
        await backend.StopAsync(stopBudget);
        logger.LogInformation("Cockpit.Server stopped.");
        return 0;
    }

    // Cancelled, so the runtime does not end the process before the stop has run.
    private static void _RequestStop(PosixSignalContext context, TaskCompletionSource stopRequested)
    {
        context.Cancel = true;
        stopRequested.TrySetResult();
    }

    private static TimeSpan _StopBudget(ILogger logger)
    {
        var value = Environment.GetEnvironmentVariable(StopBudgetVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultStopBudget;
        }

        if (int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        logger.LogWarning("{Variable} is not a positive whole number of seconds; using {Budget}.", StopBudgetVariable, DefaultStopBudget);
        return DefaultStopBudget;
    }

    private static bool _IsUi(string name) =>
        name.StartsWith("Avalonia", StringComparison.Ordinal)
        || name == "Cockpit.App"
        || (name.StartsWith("Cockpit.", StringComparison.Ordinal) && name.EndsWith(".UI", StringComparison.Ordinal));
}
