using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.App.ViewModels.Onboarding;
using Cockpit.App.ViewTests;
using Cockpit.App.Views.Onboarding;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Backup;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Backup;
using Cockpit.Core.Mcp;
using Cockpit.Core.Plugins;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Plugins;
using Cockpit.TestSupport;
using CockpitApp = Cockpit.App.App;

namespace Cockpit.Journeys;

// J9, the very first start: a machine that has never run the wizard opens it before the cockpit, asks how to begin,
// and "Start fresh" walks the other steps into the cockpit. The wizard is shown before the plugins' phase, like
// App's first-run route.
[Collection(JourneyCollection.Alone)]
public sealed class FirstStartJourney
{
    // A first machine walks "Start fresh" and backs its cockpit up. AC-1509, route C1: a second machine's very first start
    // offers the bundled Depot's route; signing in to a fake Depot holding that backup and restoring it restarts into the
    // cockpit it was made of, asking for the sign-in the backup left out (AC-1503).
    [Fact]
    public async Task AFreshMachine_StartsFresh_AndASecondSignsInToItsDepot_AndRestoresItsBackup_IntoTheCockpitTheFirstWasMadeOf()
    {
        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"journey-first-start-{Guid.NewGuid():N}")).FullName;
        try
        {
            await Step.Run("Gemini and Codex are installed, then the operator chooses the assistant after restart", async () =>
            {
                var stateRoot = Directory.CreateDirectory(Path.Combine(work, "assistant-choice")).FullName;
                var restarts = new _Restarts();
                Task firstShown = Task.CompletedTask;
                await using (var first = JourneyHost.Desktop(
                    root => File.WriteAllText(CockpitConfigPath.For(root), """{"SeededBundledPlugins":["claude-provider","cli-agent-provider","gemini-provider"]}"""),
                    stateRoot,
                    services =>
                    {
                        services.AddSingleton<IPluginProvisioningService>(new _ProviderProvisioner());
                        services.AddSingleton<IAppRestartService>(restarts);
                    }))
                {
                    var opened = new TaskCompletionSource<FirstRunWizardWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var watch = Window.WindowOpenedEvent.AddClassHandler<FirstRunWizardWindow>((wizard, _) => opened.TrySetResult(wizard));
                    await HeadlessAvalonia.RunAsync(() =>
                    {
                        firstShown = first.Services.GetRequiredService<IFirstRunWizard>().ShowAsync();
                        return Task.CompletedTask;
                    });
                    var window = await opened.Task.WaitAsync(Until.Ceiling);
                    var wizard = HeadlessAvalonia.Run(() => Assert.IsType<FirstRunWizardViewModel>(window.DataContext));
                    var providers = first.Services.GetServices<IFirstRunWizardStep>().OfType<ProviderStep>().Single();
                    var dialogs = Assert.IsType<SessionDialogService>(first.Services.GetRequiredService<ISessionDialogService>());
                    dialogs.OwnerWindow = () => window;

                    await HeadlessAvalonia.RunAsync(() =>
                    {
                        wizard.NextCommand.Execute(null);
                        wizard.NextCommand.Execute(null);
                        var viewModel = providers.ViewModel ?? throw new InvalidOperationException("The provider step has no view model.");
                        viewModel.Providers.Clear();

                        ProviderPickerRowViewModel Row(string id, string name, ProviderDetectionState detection) => new(
                            new Cockpit.App.ViewModels.StorePluginRowViewModel(
                                new PluginStoreEntry(id, name, name, "Cockpit", "1.0.0", [new PluginStoreVersion("1.0.0", $"{id}.zip", 3, null, null, null)], PluginStoreEntry.ProviderCategory),
                                PluginStoreConfig.Remote("https://store.invalid"), null),
                            detection);

                        var claude = Row("claude-provider", "Claude Code", ProviderDetectionState.Found);
                        Assert.False(claude.IsSelected);
                        viewModel.Providers.Add(claude);
                        var gemini = Row("gemini-provider", "Gemini / OpenAI Provider", ProviderDetectionState.NotApplicable);
                        gemini.IsSelected = true;
                        viewModel.Providers.Add(gemini);
                        var codex = Row("cli-agent-provider", "Codex (ChatGPT)", ProviderDetectionState.NotFound);
                        codex.IsSelected = true;
                        viewModel.Providers.Add(codex);
                        return Task.CompletedTask;
                    });

                    var consentOpened = new TaskCompletionSource<PluginConsentDialog>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var consentWatch = Window.WindowOpenedEvent.AddClassHandler<PluginConsentDialog>((dialog, _) => consentOpened.TrySetResult(dialog));
                    string? geminiConsentSha = null;
                    var install = HeadlessAvalonia.RunAsync(async () =>
                    {
                        var viewModel = providers.ViewModel ?? throw new InvalidOperationException("The provider step has no view model.");
                        await viewModel.InstallSelectedCommand.ExecuteAsync(null);
                    });
                    for (var index = 0; index < 2; index++)
                    {
                        if (await Task.WhenAny(consentOpened.Task, install) == install)
                        {
                            await install;
                            var message = providers.ViewModel?.SummaryMessage ?? "The provider step has no message.";
                            throw new InvalidOperationException($"Consent question {index + 1} did not open: {message}");
                        }

                        var consent = await consentOpened.Task.WaitAsync(Until.Ceiling);
                        consentOpened = new TaskCompletionSource<PluginConsentDialog>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var consentInfo = HeadlessAvalonia.Run(() => Assert.IsType<PluginConsentInfo>(consent.DataContext));
                        if (index == 0)
                        {
                            var livePlugin = (await first.Services.GetRequiredService<IPluginAdministration>().GetInstalledAsync())
                                .Single(plugin => plugin.Discovered.FolderId == "gemini-provider");
                            Assert.NotEqual(livePlugin.Discovered.Sha256, consentInfo.Sha256);
                            geminiConsentSha = consentInfo.Sha256;
                        }

                        await HeadlessAvalonia.RunAsync(() =>
                        {
                            consent.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "Enable"))
                                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            return Task.CompletedTask;
                        });
                    }
                    await install;

                    Assert.True(restarts.Asked);
                    HeadlessAvalonia.Run(window.Close);
                    await firstShown.WaitAsync(Until.Ceiling);
                    var saved = Assert.IsType<FirstRunWizardResumeState>(
                        await first.Services.GetRequiredService<IFirstRunWizardStateStore>().GetResumeStateAsync());
                    Assert.Equal(25, saved.StartAtOrder);
                    Assert.Null(saved.AssistantProviderId);
                    Assert.Equal(["gemini-provider", "cli-agent-provider"], saved.Providers.Select(provider => provider.Id));
                    var installed = await first.Services.GetRequiredService<IPluginAdministration>().GetInstalledAsync();
                    Assert.Equal(
                        geminiConsentSha,
                        installed.Single(plugin => plugin.Discovered.FolderId == "gemini-provider").Registration?.PinnedSha256);
                }

                await using var restarted = JourneyHost.Desktop(stateRoot: stateRoot);
                var resumedOpened = new TaskCompletionSource<FirstRunWizardWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var resumedWatch = Window.WindowOpenedEvent.AddClassHandler<FirstRunWizardWindow>((wizard, _) => resumedOpened.TrySetResult(wizard));
                Task resumedShown = Task.CompletedTask;
                await HeadlessAvalonia.RunAsync(() =>
                {
                    resumedShown = restarted.Services.GetRequiredService<IFirstRunWizard>().ShowAsync();
                    return Task.CompletedTask;
                });
                var resumedWindow = await resumedOpened.Task.WaitAsync(Until.Ceiling);
                var resumedWizard = HeadlessAvalonia.Run(() => Assert.IsType<FirstRunWizardViewModel>(resumedWindow.DataContext));
                var assistant = restarted.Services.GetServices<IFirstRunWizardStep>().OfType<AssistantStep>().Single().ViewModel;
                Assert.Equal("Your assistant", HeadlessAvalonia.Run(() => resumedWizard.StepBar.Single(step => step.IsCurrent).Title));
                Assert.False(assistant.AssistantOptions.IsEnabled);
                Assert.Null(assistant.DeferredAssistantName);
                Assert.False(assistant.OnlyLocalProvidersAvailable);

                var profileOpened = new TaskCompletionSource<AssistantProfileDialog>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var profileWatch = Window.WindowOpenedEvent.AddClassHandler<AssistantProfileDialog>((dialog, _) => profileOpened.TrySetResult(dialog));
                var resumedDialogs = Assert.IsType<SessionDialogService>(restarted.Services.GetRequiredService<ISessionDialogService>());
                resumedDialogs.OwnerWindow = () => resumedWindow;
                Task profileShown = Task.CompletedTask;
                await HeadlessAvalonia.RunAsync(() =>
                {
                    profileShown = assistant.EditProfileCommand.ExecuteAsync(null);
                    return Task.CompletedTask;
                });
                var profileDialog = await profileOpened.Task.WaitAsync(Until.Ceiling);
                var providerPicker = HeadlessAvalonia.Run(() => profileDialog.GetLogicalDescendants().OfType<ComboBox>()
                    .Single(combo => combo.ItemsSource?.Cast<SessionProviderOption>().Any() == true));
                var providerOptions = HeadlessAvalonia.Run(() => providerPicker.ItemsSource?.Cast<SessionProviderOption>().ToList() ?? []);
                Assert.Equal(["Ollama", "LM Studio", "Gemini", "OpenAI", "Codex (CLI)"], providerOptions.Select(option => option.Label));
                Assert.DoesNotContain(providerOptions, option => option.Label.Contains("Claude", StringComparison.Ordinal));
                Assert.Null(HeadlessAvalonia.Run(() => providerPicker.SelectedItem));
                var profileViewModel = HeadlessAvalonia.Run(() => Assert.IsType<AssistantProfileDialogViewModel>(profileDialog.DataContext));
                HeadlessAvalonia.Run(() =>
                {
                    providerPicker.SelectedItem = providerOptions.Single(option => option.Label == "Gemini");
                    Dispatcher.UIThread.RunJobs();
                });
                Assert.True(profileViewModel.CanSetUpLater);
                Assert.True(profileViewModel.Profile.IsPluginProviderMissing);
                await HeadlessAvalonia.RunAsync(() => profileViewModel.SetUpLaterCommand.ExecuteAsync(null));
                await profileShown.WaitAsync(Until.Ceiling);
                Assert.True(assistant.AssistantOptions.IsEnabled);
                Assert.Equal("Gemini", assistant.DeferredAssistantName);
                var deferred = await restarted.Services.GetRequiredService<IFirstRunWizardStateStore>().GetResumeStateAsync();
                Assert.Equal("gemini-provider.gemini", deferred?.AssistantProviderId);

                await HeadlessAvalonia.RunAsync(() =>
                {
                    while (!resumedWizard.IsLastStep)
                    {
                        resumedWizard.NextCommand.Execute(null);
                    }

                    resumedWizard.NextCommand.Execute(null);
                    Dispatcher.UIThread.RunJobs();
                    return Task.CompletedTask;
                });
                await resumedShown.WaitAsync(Until.Ceiling);
                Assert.Null(await restarted.Services.GetRequiredService<IFirstRunWizardStateStore>().GetResumeStateAsync());

                await restarted.StartDesktopAsync();
                var slot = await restarted.Services.GetRequiredService<IAssistantProfileStore>().LoadAsync();
                var provider = Assert.IsType<PluginProviderConfig>(slot.Profile?.ProviderConfig);
                Assert.Equal("gemini-provider.gemini", provider.ProviderId);
                Assert.True((await restarted.Services.GetRequiredService<IAssistantSettingsStore>().LoadAsync()).IsEnabled);
            });

            var archive = Path.Combine(work, "made-elsewhere.zip");
            await Step.Run("a fresh machine starts fresh and backs its cockpit up", async () =>
            {
                await using (var cockpit = JourneyHost.Desktop(root => File.WriteAllText(CockpitConfigPath.For(root), MadeElsewhere), Directory.CreateDirectory(Path.Combine(work, "elsewhere")).FullName))
                {
                    var services = cockpit.Services;
                    var state = services.GetRequiredService<IFirstRunWizardStateStore>();
                    Assert.Null(await state.GetCompletedVersionAsync());

                    var opened = new TaskCompletionSource<FirstRunWizardWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var watch = Window.WindowOpenedEvent.AddClassHandler<FirstRunWizardWindow>((wizard, _) => opened.TrySetResult(wizard));
                    Task shown = Task.CompletedTask;
                    await HeadlessAvalonia.RunAsync(() =>
                    {
                        shown = services.GetRequiredService<IFirstRunWizard>().ShowAsync();
                        return Task.CompletedTask;
                    });
                    var window = await opened.Task.WaitAsync(Until.Ceiling);
                    var wizard = HeadlessAvalonia.Run(() => Assert.IsType<FirstRunWizardViewModel>(window.DataContext));

                    // The choice step is the second page, with the host's own two ways and no plugin's yet.
                    var choice = services.GetServices<IFirstRunWizardStep>().OfType<StartChoiceStep>().Single();
                    var (steps, titles, label) = HeadlessAvalonia.Run(() =>
                    {
                        wizard.NextCommand.Execute(null);
                        return (wizard.StepBar.Select(item => item.Title).ToList(), choice.ViewModel.Options.Select(option => option.Title).ToList(), wizard.StepProgressLabel);
                    });
                    Assert.Equal(["What this is", "Getting started", "What you have", "Your assistant", "What you work on"], steps);
                    Assert.Equal(["Start fresh", "Restore a backup file"], titles);
                    Assert.Equal("Step 2 of 5", label);

                    // Start fresh is the pick nobody has to make: Next carries on through every step, and the last Next ends it.
                    // AC-1671: on the assistant step Next waits while the assistant is on without a profile; Back and switching it off stay.
                    await HeadlessAvalonia.RunAsync(() =>
                    {
                        var assistant = services.GetServices<IFirstRunWizardStep>().OfType<AssistantStep>().Single();
                        wizard.NextCommand.Execute(null);

                        // AC-1671: on "What you have" Next waits while something is ticked and not installed; Back stays, unticking frees it.
                        var providers = services.GetServices<IFirstRunWizardStep>().OfType<ProviderStep>().Single().ViewModel ?? throw new InvalidOperationException("The provider step has no view model.");
                        var claude = new Cockpit.App.ViewModels.Onboarding.ProviderPickerRowViewModel(
                            new Cockpit.App.ViewModels.StorePluginRowViewModel(new Cockpit.Core.Plugins.PluginStoreEntry("claude-provider", "Claude Code", "d", "Cockpit", "1.0.0", []), Cockpit.Core.Plugins.PluginStoreConfig.Remote("url"), null),
                            Cockpit.App.ViewModels.Onboarding.ProviderDetectionState.NotApplicable);
                        providers.Providers.Add(claude);
                        claude.IsSelected = true;
                        Assert.False(wizard.NextCommand.CanExecute(null));
                        Assert.True(wizard.BackCommand.CanExecute(null));
                        claude.IsSelected = false;
                        Assert.True(wizard.NextCommand.CanExecute(null));
                        providers.Providers.Clear();
                        wizard.NextCommand.Execute(null);
                        assistant.ViewModel.AssistantOptions.IsEnabled = true;
                        Assert.False(wizard.NextCommand.CanExecute(null));
                        Assert.True(wizard.BackCommand.CanExecute(null));
                        assistant.ViewModel.AssistantOptions.IsEnabled = false;
                        Assert.True(wizard.NextCommand.CanExecute(null));

                        while (!wizard.IsLastStep)
                        {
                            wizard.NextCommand.Execute(null);
                        }

                        wizard.NextCommand.Execute(null);
                        Dispatcher.UIThread.RunJobs();
                        return Task.CompletedTask;
                    });
                    await shown.WaitAsync(Until.Ceiling);
                    Assert.Equal(FirstRunWizardVersion.Current, await state.GetCompletedVersionAsync());

                    await cockpit.StartDesktopAsync();
                    Assert.True(HeadlessAvalonia.Run(() => cockpit.Window.IsVisible));

                    // The cockpit it walked into is the one the second machine gets back: its backup, without the sign-ins.
                    await services.GetRequiredService<IPluginRegistrationStore>().SaveDataAsync("depot", new Dictionary<string, string> { ["connections"] = """{"token":"journey-secret"}""" });
                    await services.GetRequiredService<IBackupService>().WriteAsync(archive, new BackupOptions(IncludeCredentials: false));
                }
            });

            await Step.Run("a second machine signs in to its Depot and restores that backup", async () =>
            {
                var space = new _AccountSpace(await File.ReadAllBytesAsync(archive));
                await using var depot = await LoopbackHttpServer.StartAsync(space.HandleAsync);
                var restarts = new _Restarts();
                var fresh = Directory.CreateDirectory(Path.Combine(work, "fresh")).FullName;
                await using (var cockpit = JourneyHost.Desktop(stateRoot: fresh, configure: services =>
                {
                    services.AddSingleton<IMcpOAuthCoordinator>(provider => new _SignedInOnClick(provider.GetRequiredService<IMcpOAuthTokenStore>(), depot.BaseUrl));
                    services.AddSingleton<IAppRestartService>(restarts);
                }))
                {
                    var services = cockpit.Services;
                    HeadlessAvalonia.Run(CockpitApp.InitializeFirstStart);

                    var opened = new TaskCompletionSource<FirstRunWizardWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var watch = Window.WindowOpenedEvent.AddClassHandler<FirstRunWizardWindow>((wizard, _) => opened.TrySetResult(wizard));
                    await HeadlessAvalonia.RunAsync(() =>
                    {
                        _ = services.GetRequiredService<IFirstRunWizard>().ShowAsync();
                        return Task.CompletedTask;
                    });
                    var window = await opened.Task.WaitAsync(Until.Ceiling);
                    var choice = services.GetServices<IFirstRunWizardStep>().OfType<StartChoiceStep>().Single();
                    var view = HeadlessAvalonia.Run(() =>
                    {
                        ((FirstRunWizardViewModel)(window.DataContext ?? throw new InvalidOperationException("The wizard has no view model."))).NextCommand.Execute(null);
                        Assert.Equal(["Start fresh", "Restore a backup file", "Sign in to a Depot"], choice.ViewModel.Options.Select(option => option.Title));
                        choice.ViewModel.ChooseCommand.Execute(choice.ViewModel.Options[2]);
                        return choice.ViewModel.SelectedContent ?? throw new InvalidOperationException("The Depot route shows nothing.");
                    });

                    await HeadlessAvalonia.RunAsync(async () =>
                    {
                        _Named<TextBox>(view, "DepotFirstStartUrl").Text = depot.BaseUrl;
                        var status = _Named<TextBlock>(view, "DepotFirstStartStatus");
                        _Named<Button>(view, "DepotFirstStartSignIn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Until.Holds(status, () => status.Text?.StartsWith("Signed in to", StringComparison.Ordinal) == true);

                        // One backup there and no /cockpit/manifest: C1 is offered, C2 is not.
                        var restore = _Named<Button>(view, "DepotFirstStartRestore");
                        Assert.True(restore.IsEnabled);
                        Assert.False(_Named<Button>(view, "DepotFirstStartSetUp").IsVisible);
                        restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        var outcome = _Named<TextBlock>(view, "DepotFirstStartOutcome");
                        await Until.Holds(outcome, () => outcome.Text?.StartsWith("Restored.", StringComparison.Ordinal) == true);
                    });

                    Assert.True(restarts.Asked);
                    Assert.Equal(FirstRunWizardVersion.Current, await services.GetRequiredService<IFirstRunWizardStateStore>().GetCompletedVersionAsync());
                }

                await using var restarted = JourneyHost.Desktop(stateRoot: fresh);
                await restarted.StartDesktopAsync();
                Assert.Equal(333, JsonNode.Parse(await File.ReadAllTextAsync(CockpitConfigPath.For(fresh)))?["Layout"]?["SidebarWidth"]?.GetValue<double>());
                Assert.Contains(HeadlessAvalonia.Run(() => restarted.Cockpit.Toasts.Select(toast => toast.Message).ToList()),
                    message => message.Contains("new sign-in", StringComparison.Ordinal) && message.Contains("depot", StringComparison.OrdinalIgnoreCase));
            });

            // AC-1670: an organisation that only has ids in its manifest is still named, what the setup holds is listed
            // before the choice, nothing collapses, Next waits for the choice, and the outcome stays on screen after it.
            await Step.Run("a third machine sees what its organisation sets up, sets it up and reads the outcome", async () =>
            {
                var space = new _AccountSpace([], OrganisationManifest);
                await using var depot = await LoopbackHttpServer.StartAsync(space.HandleAsync);
                await using var cockpit = JourneyHost.Desktop(stateRoot: Directory.CreateDirectory(Path.Combine(work, "organisation")).FullName, configure: services =>
                {
                    services.AddSingleton<IMcpOAuthCoordinator>(provider => new _SignedInOnClick(provider.GetRequiredService<IMcpOAuthTokenStore>(), depot.BaseUrl));
                });
                var services = cockpit.Services;
                HeadlessAvalonia.Run(CockpitApp.InitializeFirstStart);

                var opened = new TaskCompletionSource<FirstRunWizardWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var watch = Window.WindowOpenedEvent.AddClassHandler<FirstRunWizardWindow>((wizard, _) => opened.TrySetResult(wizard));
                await HeadlessAvalonia.RunAsync(() =>
                {
                    _ = services.GetRequiredService<IFirstRunWizard>().ShowAsync();
                    return Task.CompletedTask;
                });
                var window = await opened.Task.WaitAsync(Until.Ceiling);
                var choice = services.GetServices<IFirstRunWizardStep>().OfType<StartChoiceStep>().Single();
                var (wizard, view) = HeadlessAvalonia.Run(() =>
                {
                    var wizard = (FirstRunWizardViewModel)(window.DataContext ?? throw new InvalidOperationException("The wizard has no view model."));
                    wizard.NextCommand.Execute(null);
                    choice.ViewModel.ChooseCommand.Execute(choice.ViewModel.Options[2]);
                    return (wizard, choice.ViewModel.SelectedContent ?? throw new InvalidOperationException("The Depot route shows nothing."));
                });
                Assert.False(HeadlessAvalonia.Run(() => wizard.NextCommand.CanExecute(null)));

                await HeadlessAvalonia.RunAsync(async () =>
                {
                    _Named<TextBox>(view, "DepotFirstStartUrl").Text = depot.BaseUrl;
                    var status = _Named<TextBlock>(view, "DepotFirstStartStatus");
                    _Named<Button>(view, "DepotFirstStartSignIn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until.Holds(status, () => status.Text?.StartsWith("Signed in to", StringComparison.Ordinal) == true);

                    view.GetLogicalDescendants().OfType<Button>().First(button => button.Name == "DepotFirstStartSetUpOrganization").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until.LayoutHolds(view, () => view.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "DepotFirstStartAgree" && button.IsEffectivelyVisible));
                    var agree = _Named<Button>(view, "DepotFirstStartAgree");
                    var shown = string.Join(" | ", view.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text));
                    Assert.Contains("from Acme", shown);
                    Assert.Contains("Profiles (1): Acme dev", shown);
                    Assert.Contains("Settings (2)", shown);
                    Assert.DoesNotContain(OrganisationId, shown);
                    Assert.DoesNotContain("From:", shown);

                    // A click on the summary collapses nothing: the screen stays the one that asks.
                    var summary = view.GetLogicalDescendants().OfType<TextBlock>().First(text => text.Text?.StartsWith("Profiles", StringComparison.Ordinal) == true);
                    summary.RaiseEvent(new RoutedEventArgs(Avalonia.Input.InputElement.TappedEvent));
                    Assert.True(agree.IsEffectivelyVisible);
                    Assert.False(wizard.NextCommand.CanExecute(null));
                    Assert.True(wizard.BackCommand.CanExecute(null));

                    agree.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var outcome = _Named<TextBlock>(view, "DepotFirstStartOutcome");
                    await Until.Holds(outcome, () => outcome.Text?.StartsWith("Set up: 1 profile, 2 settings.", StringComparison.Ordinal) == true);
                    Assert.True(outcome.IsEffectivelyVisible);
                    Assert.True(wizard.NextCommand.CanExecute(null));
                });
            });
        }
        finally
        {
            JourneyHost.RemoveStateRoot(work);
        }
    }

    private sealed class _ProviderProvisioner : IPluginProvisioningService
    {
        public Task<PluginProvisionResult> InstallAsync(
            PluginProvisionRequest request,
            int hostAbstractionsMajor,
            Version? hostVersion = null,
            CancellationToken cancellationToken = default) =>
            _InstallAsync(request, cancellationToken);

        public async Task<PluginProvisionBatchResult> InstallManyAsync(
            IReadOnlyList<PluginProvisionRequest> requests,
            int hostAbstractionsMajor,
            Version? hostVersion = null,
            CancellationToken cancellationToken = default)
        {
            var results = new List<PluginProvisionResult>();
            foreach (var request in requests)
            {
                results.Add(await _InstallAsync(request, cancellationToken));
            }

            return new PluginProvisionBatchResult(results);
        }

        private static async Task<PluginProvisionResult> _InstallAsync(
            PluginProvisionRequest request,
            CancellationToken cancellationToken)
        {
            var source = Path.Combine(AppContext.BaseDirectory, BundledPluginInstaller.BundledFolderName, request.Id);
            var target = Path.Combine(PluginBootstrap.PluginsRoot, request.Id);
            var staged = request.Id == "gemini-provider";
            var installedTarget = staged
                ? Path.Combine(PluginBootstrap.PluginsRoot, ".pending-updates", request.Id)
                : target;
            _CopyPlugin(source, installedTarget);

            if (staged)
            {
                _CopyPlugin(source, target);
                await File.WriteAllTextAsync(Path.Combine(target, "old-marker.txt"), "old bytes", cancellationToken);
            }

            var sha256 = await PluginClosureHash.OfInstalledFolderAsync(installedTarget, cancellationToken);
            var manifestJson = await File.ReadAllTextAsync(Path.Combine(source, "plugin.json"), cancellationToken);
            if (!PluginManifest.TryParse(manifestJson, out var manifest, out var error) || manifest is null)
            {
                throw new InvalidOperationException(error ?? $"Could not parse {request.Id}'s manifest.");
            }

            return new PluginProvisionResult(
                staged ? PluginProvisionOutcome.Staged : PluginProvisionOutcome.Installed,
                request.Id,
                request.Name,
                null,
                null,
                request.Id,
                sha256,
                manifest);
        }

        private static void _CopyPlugin(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }
        }
    }

    private const string OrganisationId = "019f4da0d22e77d79401467fffa3c1ad";

    // What Depot issues: the issuer carries ids only.
    private const string OrganisationManifest = """
        {
          "format": "cockpit.provision", "formatVersion": 1,
          "issuer": { "kind": "depot", "organization": { "id": "019f4da0d22e77d79401467fffa3c1ad" }, "profile": { "id": "019f4da0d22e77d79401467fffa3c1ae" } },
          "settings": {
            "terminal.fontSize":  { "value": 15,  "mode": "default", "by": "org" },
            "layout.sidebarWidth": { "value": 300, "mode": "default", "by": "org" }
          },
          "profiles": { "items": [ { "id": "acme-dev", "label": "Acme dev", "by": "org", "provider": { "id": "claude-provider", "model": "claude-sonnet-5-5" } } ] }
        }
        """;

    // A cockpit with a sidebar width of its own. Its Depot sign-in is seeded after the build, so the bundled plugin installs.
    private const string MadeElsewhere = """{"Layout":{"SidebarWidth":333}}""";

    private static T _Named<T>(Control root, string name)
        where T : Control =>
        root.GetLogicalDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"The Depot route has no {name}.");

    // The browser's half of the sign-in, which a journey cannot run: the click signs in, and the bearer is the fake's. It
    // stores the sign-in as a real one does, so the host narrows its depot.host token from it at the fake's token endpoint.
    private sealed class _SignedInOnClick(IMcpOAuthTokenStore tokens, string url) : IMcpOAuthCoordinator
    {
        public event Action<McpServerConfig>? SignInNeeded
        {
            add { }
            remove { }
        }

        public event Action<McpServerConfig>? AccountChanged
        {
            add { }
            remove { }
        }

        public async Task<McpOAuthAccess> AcquireAsync(McpServerConfig server, bool interactive, CancellationToken cancellationToken = default)
        {
            await tokens.SaveAsync(server.IdentityKey, server.Name, DepotHostScope.SignedIn(_AccountSpace.Bearer, url.TrimEnd('/')), cancellationToken);
            return McpOAuthAccess.Authorized(_AccountSpace.Bearer);
        }

        public Task<McpOAuthAccess> AcquireForSessionAsync(McpServerConfig server, CancellationToken cancellationToken = default) =>
            Task.FromResult(McpOAuthAccess.Authorized(_AccountSpace.Bearer));

        public Task<McpOAuthAccess> RenewRejectedAsync(McpServerConfig server, string rejectedAccessToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(McpOAuthAccess.AuthorizationRequired);

        public Task<McpAuthState> GetStateAsync(McpServerConfig server, CancellationToken cancellationToken = default) =>
            Task.FromResult(McpAuthState.Authorized);

        public Task SignOutAsync(McpServerConfig server, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class _Restarts : IAppRestartService
    {
        public bool Asked { get; private set; }

        public void Restart() => Asked = true;
    }

    // Depot's /cockpit/backups (DEP-168) holding one backup; /cockpit/manifest is not there, as on a Depot without DEP-266.
    private sealed class _AccountSpace(byte[] backup, string? manifest = null)
    {
        public const string Bearer = "journey-first-start-bearer";

        private static readonly Guid Id = Guid.NewGuid();

        public async Task HandleAsync(HttpContext context)
        {
            if (await DepotHostScope.TryAnswerAsync(context, Bearer))
            {
                return;
            }

            var path = context.Request.Path;
            if (context.Request.Headers.Authorization != $"Bearer {Bearer}")
            {
                context.Response.StatusCode = 401;
            }
            else if (path == "/cockpit/backups")
            {
                await context.Response.WriteAsJsonAsync(new[] { new { id = Id, size = backup.LongLength, sha256 = "", createdAt = DateTime.UtcNow } });
            }
            else if (path == "/cockpit/manifest" && manifest is not null)
            {
                if (context.Request.Query.ContainsKey("org"))
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(manifest);
                }
                else
                {
                    context.Response.StatusCode = 300;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        format = "cockpit.provision.choice",
                        formatVersion = 1,
                        organizations = new[] { new { slug = "acme", name = "Acme" }, new { slug = "other", name = "Other" } },
                    });
                }
            }
            else if (path == $"/cockpit/backups/{Id}")
            {
                await context.Response.Body.WriteAsync(backup);
            }
            else
            {
                context.Response.StatusCode = 404;
            }
        }
    }
}
