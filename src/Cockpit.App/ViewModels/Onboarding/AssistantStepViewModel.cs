using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.App.ViewModels.Onboarding;

// Drives the first-run wizard's assistant step (AC-585): reuses the exact settings/profile Options → Voice →
// Assistant already edits, so there is no second definition (criterion 3). Never touches consent-bypass
// (AC-575/AC-637) — the hard boundary the ticket draws.
public sealed partial class AssistantStepViewModel : ObservableObject
{
    private readonly ISessionDialogService? _dialogService;

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
    public bool OnlyLocalProvidersAvailable { get; }

    // Design-time/previewer constructor.
    public AssistantStepViewModel()
        : this(null, null, null, null)
    {
    }

    public AssistantStepViewModel(
        IAssistantSettingsStore? settingsStore,
        IAssistantProfileStore? profileStore,
        ISessionDialogService? dialogService,
        IPluginProviderRegistry? pluginProviderRegistry)
    {
        _dialogService = dialogService;
        AssistantOptions = new AssistantOptionsViewModel(settingsStore, profileStore);
        OnlyLocalProvidersAvailable = pluginProviderRegistry is null || pluginProviderRegistry.Registrations.Count == 0;

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
