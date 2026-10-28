using Avalonia.Headless;
using Avalonia.Threading;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App;
using Cockpit.App.ViewModels;
using Cockpit.App.ViewModels.Onboarding;
using Cockpit.App.ViewTests;
using Cockpit.App.Views.Onboarding;
using Cockpit.Core.Toasts;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Backup;
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
        "autopilot", "claude-provider", "cli-agent-provider", "clock", "depot", "example-companion-tool", "example-workspace", "fan-out", "gemini-provider", "git-status",
        "schedules", "transcript-search", "usage-trend",
    ];

    [Fact]
    public async Task TheCockpitStarts_WithEveryBundledPlugin_RefusingOneBuiltForAnotherContract()
    {
        await using var cockpit = JourneyHost.Desktop(stateRoot =>
        {
            _InstallAPluginForContractTwo(stateRoot);
            File.WriteAllText(Path.Combine(stateRoot, "restored-signins.json"), """[{"Label":"Depot","PluginId":"depot"},{"Label":"YouTrack MCP","PluginId":null}]""");
        });
        var services = cockpit.Services;
        await services.GetRequiredService<IPluginRegistrationStore>().SaveDataAsync("schedules", new Dictionary<string, string> { ["reminders"] = _MissedReminder() });

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
        Assert.Subset(steps.ToHashSet(), new HashSet<Type> { typeof(WelcomeStep), typeof(WorkKindStep), typeof(StartChoiceStep), typeof(ProviderStep) });

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

        // AC-1803: what posts while the plugins start (two restored sign-ins, a missed reminder) shows as one toast,
        // and its Show opens the panel where each notification keeps its own live buttons.
        var center = cockpitViewModel.Notifications;
        await HeadlessAvalonia.RunAsync(() => Until.Holds(center, () => center.Items.Count == 3));
        await HeadlessAvalonia.RunAsync(() =>
        {
            var startupToast = Assert.Single(cockpitViewModel.Toasts);
            Assert.Equal("3 notifications at startup", startupToast.Message);
            startupToast.Actions[0].InvokeCommand.Execute(null);
            Assert.Equal(
                [EmptiedSignIn.Ask("Depot"), EmptiedSignIn.Ask("YouTrack MCP")],
                center.Items.Where(item => item.Source != "Schedules").Select(item => item.Message).Order());
            Assert.Equal(["Done", "Snooze 10 min", "1 hour"], center.Items.Single(item => item.Source == "Schedules").Actions.Select(action => action.Label));
            center.Items.Single(item => item.Source == "Schedules").Actions[0].InvokeCommand.Execute(null);
            return Task.CompletedTask;
        });
        Assert.Equal(NotificationCenterViewModel.DockPanelId, cockpitViewModel.OpenDockPanelId);
        var reminders = "";
        await Until.ReloadHolds(
            async () => reminders = JsonSerializer.Deserialize<string>((await services.GetRequiredService<IPluginRegistrationStore>().LoadDataAsync("schedules"))["reminders"]) ?? "",
            () => reminders.Contains("\"State\":\"Done\"", StringComparison.Ordinal));

        await HeadlessAvalonia.RunAsync(() =>
        {
            cockpitViewModel.ToastHost.EndStartupNotificationWindow();
            cockpitViewModel.ToastHost.Add("A mid-session notification", ToastSeverity.Warning, [new ToastAction("Acknowledge", static () => { })], sticky: true);
            Assert.Equal("A mid-session notification", Assert.Single(cockpitViewModel.Toasts).Message);
            return Task.CompletedTask;
        });
    }

    // Installed as the store would leave it; its contract is refused before its assembly is ever read.
    // A one-off reminder five minutes overdue, in the Schedules storage shape: the engine's first tick says it.
    private static string _MissedReminder() => JsonSerializer.Serialize(JsonSerializer.Serialize(new[]
    {
        new { Id = "missed", Text = "Stand-up", Schedule = "once 2026-01-01 08:00", TimeZone = "UTC", State = "Open", Slot = DateTimeOffset.UtcNow.AddMinutes(-5), Delivery = new[] { new { Channel = "cockpit" } } },
    }));

    private static void _InstallAPluginForContractTwo(string stateRoot)
    {
        var folder = Directory.CreateDirectory(Path.Combine(stateRoot, "plugins", RefusedPlugin)).FullName;
        File.WriteAllText(Path.Combine(folder, "plugin.json"), $$"""
            { "id": "{{RefusedPlugin}}", "name": "Contract Two", "version": "1.0.0", "entryAssembly": "ContractTwo.dll", "abstractionsVersion": 2 }
            """);
        File.WriteAllBytes(Path.Combine(folder, "ContractTwo.dll"), []);
    }
}
