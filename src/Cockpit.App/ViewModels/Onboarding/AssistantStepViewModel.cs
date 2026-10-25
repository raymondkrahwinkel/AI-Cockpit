using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewModels.Onboarding;

// Drives the first-run wizard's assistant step (AC-585): reuses the exact settings/profile Options → Voice →
// Assistant already edits, so there is no second definition (criterion 3). Never touches consent-bypass
// (AC-575/AC-637) — the hard boundary the ticket draws.
public sealed partial class AssistantStepViewModel : ObservableObject
{
    private readonly ISessionDialogService? _dialogService;
    private readonly IPluginRegistrations? _registry;
    private readonly Func<ProviderStepViewModel?>? _chosenProviders;

    public AssistantOptionsViewModel AssistantOptions { get; }

    // Always shown the same way AC-510's own local-providers line is (ProviderStepViewModel.LocalProvidersText) — a
    // fact stated plainly, not a warning.
    public string LocalProvidersText { get; } = string.Join(" and ",
        SessionProviderCatalog.Providers
            .Where(option => option.Value is SessionProvider.Ollama or SessionProvider.LmStudio)
            .Select(option => option.Label));

    // True when nothing beyond the two built-in local providers is registered (AC-585 criterion 5) — read off the
    // same registry the assistant's own profile dialog offers providers from, not a fresh network probe. This only
    // states the fact; nothing here picks a profile because of it.
    [ObservableProperty]
    private bool _onlyLocalProvidersAvailable;

    // The providers the previous step ticked or installed. Plugins load only after the wizard, and one waits for its
    // approval, so none of them can be the assistant's provider yet; saying so beats listing only the two local ones.
    public ObservableCollection<string> ChosenProviders { get; } = [];

    public bool HasChosenProviders => ChosenProviders.Count > 0;

    // Why Next is off: the assistant is on and has no profile to run as. Skip and switching it off stay possible.
    public string? NextBlockedReason =>
        AssistantOptions.IsEnabled && string.IsNullOrEmpty(AssistantOptions.ProfileLabel) ? "Pick a profile, or turn the assistant off." : null;

    public event EventHandler? NextBlockedChanged;

    // Reads the previous step again; called when the wizard lands here.
    public void RefreshChosenProviders()
    {
        ChosenProviders.Clear();
        foreach (var line in _chosenProviders?.Invoke()?.ChosenProviderLines ?? [])
        {
            ChosenProviders.Add(line);
        }

        OnPropertyChanged(nameof(HasChosenProviders));
        OnlyLocalProvidersAvailable = ChosenProviders.Count == 0 && (_registry is null || _registry.SessionProviders.Count == 0);
    }

    // Design-time/previewer constructor.
    public AssistantStepViewModel()
        : this(null, null, null, null)
    {
    }

    public AssistantStepViewModel(
        IAssistantSettingsStore? settingsStore,
        IAssistantProfileStore? profileStore,
        ISessionDialogService? dialogService,
        IPluginRegistrations? pluginProviderRegistry,
        Func<ProviderStepViewModel?>? chosenProviders = null)
    {
        _dialogService = dialogService;
        _registry = pluginProviderRegistry;
        _chosenProviders = chosenProviders;
        AssistantOptions = new AssistantOptionsViewModel(settingsStore, profileStore);
        AssistantOptions.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(AssistantOptionsViewModel.IsEnabled) or nameof(AssistantOptionsViewModel.ProfileLabel))
            {
                NextBlockedChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        RefreshChosenProviders();

        _ = AssistantOptions.RefreshAsync();
    }

    // Opens the same dialog Options → Voice → Assistant's "Choose profile…" button opens (AC-546) — never a
    // wizard-owned copy. `null`: at first start there is no running assistant host yet, the same no-host shape the
    // design-time graph already offers no restart from.
    [RelayCommand]
    private async Task EditProfileAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await _dialogService.ShowAssistantProfileDialogAsync(null);
        await AssistantOptions.RefreshAsync();
    }
}
