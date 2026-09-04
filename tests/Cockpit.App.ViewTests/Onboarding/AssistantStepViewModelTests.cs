using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using NSubstitute;
using Cockpit.App.ViewModels;
using Cockpit.App.ViewModels.Onboarding;
using Cockpit.App.Views.Onboarding;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
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
            settingsStore: null, profileStore: profileStore, dialogService: null, pluginProviderRegistry: registry);

        Assert.True(viewModel.OnlyLocalProvidersAvailable);
        Assert.Contains("Ollama", viewModel.LocalProvidersText, StringComparison.Ordinal);
        Assert.Contains("LM Studio", viewModel.LocalProvidersText, StringComparison.Ordinal);

        await profileStore.DidNotReceive().RepointAsync(Arg.Any<SessionProfile>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await profileStore.DidNotReceive().UnsetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
