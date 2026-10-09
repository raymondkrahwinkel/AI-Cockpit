using Avalonia.Headless;
using Avalonia.Threading;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App;
using Cockpit.App.ViewModels.Onboarding;
using Cockpit.App.ViewTests;
using Cockpit.App.Views.Onboarding;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Journeys;

// J1, the first thing every day: the cockpit starts with every bundled plugin, refuses one built for another contract
// and shows its main window. Program's composition and App's plugin phase, UI parts included; no copy of either.
[Collection(JourneyCollection.Alone)]
public sealed class StartupJourney
{
    private const string RefusedPlugin = "contract-two";

    private static readonly string[] Bundled =
    [
        "autopilot", "claude-provider", "clock", "depot", "example-companion-tool", "example-workspace", "fan-out", "git-status",
        "transcript-search", "usage-trend",
    ];

    [Fact]
    public async Task TheCockpitStarts_WithEveryBundledPlugin_RefusingOneBuiltForAnotherContract()
    {
        await using var cockpit = JourneyHost.Desktop(_InstallAPluginForContractTwo);
        var services = cockpit.Services;

        await cockpit.StartDesktopAsync();
        var rendered = HeadlessAvalonia.Run(() =>
        {
            Dispatcher.UIThread.RunJobs();
            using var frame = cockpit.Window.CaptureRenderedFrame();
            return frame?.PixelSize;
        });

        // Why a plugin is missing first, by name: a failure or an approval it waits for, before the list itself.
        const string Refusal = "Built against a different Cockpit contract version than this app — update the app or reinstall the plugin build made for it.";
        var diagnostics = services.GetRequiredService<PluginDiagnostics>();
        Assert.Equal([$"{RefusedPlugin} (load): {Refusal}"], diagnostics.Failures.Select(failure => $"{failure.FolderId} ({failure.Phase}): {failure.Error}"));
        Assert.Empty(diagnostics.PendingApprovals.Select(pending => pending.ToString()));

        // And the plugin manager reads that refusal through the backend's contract (AC-1434).
        var installed = await services.GetRequiredService<IPluginAdministration>().GetInstalledAsync();
        Assert.Equal(
            [$"{RefusedPlugin}: {Refusal}"],
            installed.Where(plugin => plugin.ActivationFailure is not null).Select(plugin => $"{plugin.Discovered.FolderId}: {plugin.ActivationFailure}"));
        Assert.Equal(Bundled, services.GetRequiredService<PluginManager>().Loaded.Select(plugin => plugin.FolderId).Order());
        Assert.Contains(
            AssemblyLoadContext.All.OfType<PluginLoadContext>().SelectMany(context => context.Assemblies),
            assembly => assembly.GetName().Name == "Cockpit.Plugin.Depot.UI");
        Assert.DoesNotContain("Could not start cockpit MCP endpoint", cockpit.LogText, StringComparison.Ordinal);

        // What the plugins registered landed, and the desktop's own seams replaced the backend's defaults.
        Assert.NotNull(services.GetRequiredService<IPluginProviderRegistry>().Resolve("claude"));
        Assert.Contains("git.branch", services.GetRequiredService<IWorkflowStepRegistry>().Steps.Select(step => step.TypeId));
        Assert.Contains("cockpit-autopilot-merge-gate", services.GetRequiredService<CockpitMcpEndpointHost>().GetServers().Select(server => server.Name));
        Assert.All(
            [typeof(IUiHitchProbe), typeof(IDesktopDisplays), typeof(IExternalLinkOpener)],
            seam => Assert.Equal(typeof(Program).Assembly, services.GetRequiredService(seam).GetType().Assembly));
        Assert.NotNull(rendered);

        // What the operator reaches from the window: the backup and update buttons are live, and Help's "Run setup
        // again" opens the wizard with the steps a first run walks, the provider step among them.
        var cockpitViewModel = cockpit.Cockpit;
        Assert.True(HeadlessAvalonia.Run(() => cockpitViewModel.CanBackUp && cockpitViewModel.CanBackUpAssistantMemory && cockpitViewModel.CanCheckForUpdates));
        var steps = services.GetServices<IFirstRunWizardStep>().Select(step => step.GetType()).ToList();
        Assert.Subset(steps.ToHashSet(), new HashSet<Type> { typeof(WelcomeStep), typeof(WorkKindStep), typeof(RestoreStep), typeof(ProviderStep) });

        var opened = new TaskCompletionSource<FirstRunWizardWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = Avalonia.Controls.Window.WindowOpenedEvent.AddClassHandler<FirstRunWizardWindow>((wizard, _) => opened.TrySetResult(wizard));
        Task setup = Task.CompletedTask;
        await HeadlessAvalonia.RunAsync(() =>
        {
            setup = cockpitViewModel.RunSetupAgainCommand.ExecuteAsync(null);
            return Task.CompletedTask;
        });
        var window = await opened.Task.WaitAsync(Until.Ceiling);
        Assert.Equal(steps.Count, HeadlessAvalonia.Run(() => Assert.IsType<FirstRunWizardViewModel>(window.DataContext).StepBar.Count(item => !item.NotBuiltYet)));
        await HeadlessAvalonia.RunAsync(() =>
        {
            window.Close();
            return Task.CompletedTask;
        });
        await setup.WaitAsync(Until.Ceiling);
    }

    // Installed as the store would leave it; its contract is refused before its assembly is ever read.
    private static void _InstallAPluginForContractTwo(string stateRoot)
    {
        var folder = Directory.CreateDirectory(Path.Combine(stateRoot, "plugins", RefusedPlugin)).FullName;
        File.WriteAllText(Path.Combine(folder, "plugin.json"), $$"""
            { "id": "{{RefusedPlugin}}", "name": "Contract Two", "version": "1.0.0", "entryAssembly": "ContractTwo.dll", "abstractionsVersion": 2 }
            """);
        File.WriteAllBytes(Path.Combine(folder, "ContractTwo.dll"), []);
    }
}
