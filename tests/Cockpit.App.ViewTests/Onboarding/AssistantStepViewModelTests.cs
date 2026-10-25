using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using NSubstitute;
using Cockpit.App.Composition;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.ViewModels.Onboarding;
using Cockpit.App.Views.Onboarding;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Core.Plugins;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.ViewTests.Onboarding;

/// <summary>
/// The wizard's assistant step (AC-585): the four counterproofs the ticket names — Skip installs and starts
/// nothing, the step reuses Options → Voice → Assistant's own settings rather than defining them a second time,
/// no bypass setting is ever preselected, and a local-only-provider environment is stated rather than silently
/// filled in.
/// </summary>
[Collection("avalonia")]
public class AssistantStepViewModelTests
{
    // Criterion 1: a fresh step, never touched by the operator, never writes anything — Skip has nothing to undo.
    [Fact]
    public async Task BuildingTheStep_NeverSavesSettings_UntilTheOperatorTouchesSomething()
    {
        var settingsStore = Substitute.For<IAssistantSettingsStore>();
        settingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new AssistantSettings());
        var profileStore = Substitute.For<IAssistantProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new AssistantProfileSlot(null, "No assistant profile has been set up yet."));

        var viewModel = new AssistantStepViewModel(settingsStore, profileStore, dialogService: null, pluginProviderRegistry: null);
        await viewModel.AssistantOptions.RefreshAsync();

        Assert.False(viewModel.AssistantOptions.IsEnabled);
        await settingsStore.DidNotReceive().SaveAsync(Arg.Any<AssistantSettings>(), Arg.Any<CancellationToken>());
    }

    // Criterion 3: proven, not assumed — toggling the switch on this step's own AssistantOptions and reading it
    // back through a second, independently-constructed AssistantOptionsViewModel (what Options → Voice → Assistant
    // itself builds) over the same store shows one persisted shape, not a wizard-only copy of it.
    [Fact]
    public async Task TogglingEnabled_RoundTripsThroughTheSameSettingsOptionsUses_NotASecondDefinition()
    {
        var backing = new AssistantSettings();
        var settingsStore = Substitute.For<IAssistantSettingsStore>();
        settingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(backing));
        settingsStore.SaveAsync(Arg.Any<AssistantSettings>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                backing = call.Arg<AssistantSettings>();
                return Task.CompletedTask;
            });
        var profileStore = Substitute.For<IAssistantProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new AssistantProfileSlot(null, "No assistant profile has been set up yet."));

        var stepViewModel = new AssistantStepViewModel(settingsStore, profileStore, dialogService: null, pluginProviderRegistry: null);
        await stepViewModel.AssistantOptions.RefreshAsync();

        stepViewModel.AssistantOptions.IsEnabled = true;

        var optionsPageViewModel = new AssistantOptionsViewModel(settingsStore, profileStore);
        await optionsPageViewModel.RefreshAsync();

        Assert.True(optionsPageViewModel.IsEnabled);
    }

    // The hard boundary: rendered, this step offers exactly the enable checkbox and the profile button — nothing
    // that could preselect or recommend a bypass setting or a permission mode.
    [Fact]
    public void RenderedStep_OffersOnlyTheEnableCheckboxAndTheProfileButton() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new AssistantStepViewModel();
        viewModel.AssistantOptions.IsEnabled = true;
        var view = new AssistantStepView { DataContext = viewModel };
        var window = new Window { Width = 560, Height = 420, Content = view, DataContext = viewModel };
        try
        {
            window.Show();
            window.UpdateLayout();

            var checkBoxes = window.GetVisualDescendants().OfType<CheckBox>().ToList();
            // Avalonia's CheckBox derives from ToggleButton, which derives from Button — excluded here so the
            // checkbox above does not also count as a plain button.
            var buttons = window.GetVisualDescendants().OfType<Button>().Where(button => button is not ToggleButton).ToList();
            Assert.Single(checkBoxes);
            Assert.Single(buttons);
        }
        finally
        {
            window.Close();
        }
    });

    // Criterion 5: an environment with nothing but the two built-in local providers registered says so, and
    // building/reading that fact never repoints or unsets the assistant's profile on its own.
    [Fact]
    public async Task OnlyLocalProvidersAvailable_IsStated_AndNothingAutoPicksAProfile()
    {
        var registry = Substitute.For<IPluginProviderRegistry>();
        registry.Registrations.Returns(Array.Empty<SessionProviderRegistration>());
        var profileStore = Substitute.For<IAssistantProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new AssistantProfileSlot(null, "No assistant profile has been set up yet."));

        var viewModel = new AssistantStepViewModel(
            settingsStore: null, profileStore: profileStore, dialogService: null, pluginProviderRegistry: new PluginRegistrations(registry));

        Assert.True(viewModel.OnlyLocalProvidersAvailable);
        Assert.Contains("Ollama", viewModel.LocalProvidersText, StringComparison.Ordinal);
        Assert.Contains("LM Studio", viewModel.LocalProvidersText, StringComparison.Ordinal);

        await profileStore.DidNotReceive().RepointAsync(Arg.Any<SessionProfile>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await profileStore.DidNotReceive().UnsetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        var dialogs = Substitute.For<ISessionDialogService>();
        AssistantProfileSetup? openedSetup = null;
        dialogs.ShowAssistantProfileDialogAsync(null, Arg.Any<AssistantProfileSetup>())
            .Returns(call =>
            {
                openedSetup = call.ArgAt<AssistantProfileSetup>(1);
                return Task.CompletedTask;
            });
        var noRestart = new AssistantStepViewModel(null, profileStore, dialogs, new PluginRegistrations(registry));
        await noRestart.EditProfileCommand.ExecuteAsync(null);
        Assert.Equal(["Ollama", "LM Studio"], openedSetup?.Providers.Select(provider => provider.Label));

        // AC-1741: a session default chosen in the editor travels with the deferred choice.
        FirstRunWizardResumeState? remembered = null;
        var deferring = new AssistantStepViewModel(null, profileStore, dialogs, new PluginRegistrations(registry), null, Substitute.For<IFirstRunWizardStateStore>(), state => remembered = state);
        await deferring.EditProfileCommand.ExecuteAsync(null);
        var chosen = new SessionProfile("Codex", new PluginProviderConfig("cli-agent-provider.codex", "{}"))
        {
            Defaults = new ProfileDefaults(string.Empty, string.Empty, string.Empty) { OptionDefaults = new Dictionary<string, string> { ["sandbox"] = "workspace-write" } },
        };
        await openedSetup!.SetUpLaterAsync(chosen, CancellationToken.None);
        Assert.Equal("workspace-write", remembered?.AssistantOptionDefaults?["sandbox"]);

        // AC-1673: finding a CLI is detection, not the operator choosing it. Once ticked it is counted as a choice;
        // ticked off again it is not.
        var providers = new ProviderStepViewModel();
        var claude = new ProviderPickerRowViewModel(
            new StorePluginRowViewModel(new PluginStoreEntry("claude-provider", "Claude Code", "d", "Cockpit", "1.0.0", []), PluginStoreConfig.Remote("url"), null),
            ProviderDetectionState.Found);
        providers.Providers.Add(claude);
        Assert.False(claude.IsSelected);
        Assert.Null(providers.NextBlockedReason);
        claude.IsSelected = true;
        Assert.Equal("Press Install and restart for what you ticked, or untick it.", providers.NextBlockedReason);
        var withChoice = new AssistantStepViewModel(null, profileStore, null, new PluginRegistrations(registry), () => providers);
        Assert.False(withChoice.OnlyLocalProvidersAvailable);
        Assert.Contains("Claude Code (ticked, not installed yet", Assert.Single(withChoice.ChosenProviders), StringComparison.Ordinal);
        claude.IsSelected = false;
        Assert.Null(providers.NextBlockedReason);
        withChoice.RefreshChosenProviders();
        Assert.True(withChoice.OnlyLocalProvidersAvailable);

        // AC-1671: Next waits while the assistant is on and has no profile, and Skip is not its business.
        Assert.Null(viewModel.NextBlockedReason);
        viewModel.AssistantOptions.IsEnabled = true;
        Assert.Equal("Choose a provider, or turn the assistant off.", viewModel.NextBlockedReason);
        viewModel.AssistantOptions.ProfileLabel = "Claude (sonnet)";
        Assert.Null(viewModel.NextBlockedReason);
    }
}
