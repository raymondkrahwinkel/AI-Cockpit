using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
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
    private readonly IFirstRunWizardStateStore? _wizardState;
    private readonly Action<FirstRunWizardResumeState>? _rememberDeferred;
    private readonly Task _refreshTask;
    private FirstRunWizardResumeState? _resumeState;

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

    // The providers the previous step ticked or installed. The count also decides whether the local-only note applies.
    public ObservableCollection<string> ChosenProviders { get; } = [];

    public bool HasChosenProviders => ChosenProviders.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeferredAssistant), nameof(DeferredAssistantMessage))]
    private string? _deferredAssistantName;

    public bool HasDeferredAssistant => !string.IsNullOrWhiteSpace(DeferredAssistantName);

    public string DeferredAssistantMessage => HasDeferredAssistant
        ? $"{DeferredAssistantName} will be your assistant when setup is finished."
        : string.Empty;

    // Why Next is off: the assistant is on and has no profile to run as. Skip and switching it off stay possible.
    public string? NextBlockedReason =>
        AssistantOptions.IsEnabled && string.IsNullOrEmpty(AssistantOptions.ProfileLabel) && !HasDeferredAssistant
            ? "Choose a provider, or turn the assistant off."
            : null;

    public event EventHandler? NextBlockedChanged;

    // Reads the previous step again; called when the wizard lands here.
    public void RefreshChosenProviders()
    {
        ChosenProviders.Clear();
        var lines = _resumeState is { Providers.Count: > 0 } state
            ? state.Providers
                .Select(provider => provider.Name)
            : _chosenProviders?.Invoke()?.ChosenProviderLines ?? [];
        foreach (var line in lines)
        {
            ChosenProviders.Add(line);
        }

        OnPropertyChanged(nameof(HasChosenProviders));
        OnlyLocalProvidersAvailable = !HasDeferredAssistant && ChosenProviders.Count == 0 && (_registry is null || _registry.SessionProviders.Count == 0);
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
        Func<ProviderStepViewModel?>? chosenProviders = null,
        IFirstRunWizardStateStore? wizardState = null,
        Action<FirstRunWizardResumeState>? rememberDeferred = null)
    {
        _dialogService = dialogService;
        _registry = pluginProviderRegistry;
        _chosenProviders = chosenProviders;
        _wizardState = wizardState;
        _rememberDeferred = rememberDeferred;
        AssistantOptions = new AssistantOptionsViewModel(settingsStore, profileStore);
        AssistantOptions.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(AssistantOptionsViewModel.IsEnabled) or nameof(AssistantOptionsViewModel.ProfileLabel))
            {
                NextBlockedChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        RefreshChosenProviders();

        _refreshTask = AssistantOptions.RefreshAsync();
    }

    internal void Restore(FirstRunWizardResumeState state)
    {
        _resumeState = state;
        DeferredAssistantName = state.AssistantProviderName;
        AssistantOptions.IsEnabled = HasDeferredAssistant;
        RefreshChosenProviders();
        _ = _RestoreAfterRefreshAsync();
    }

    private async Task _RestoreAfterRefreshAsync()
    {
        await _refreshTask.ConfigureAwait(true);
        if (HasDeferredAssistant)
        {
            AssistantOptions.IsEnabled = true;
            NextBlockedChanged?.Invoke(this, EventArgs.Empty);
        }
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

        await _dialogService.ShowAssistantProfileDialogAsync(null, _FirstRunSetup());
        await AssistantOptions.RefreshAsync();
    }

    private AssistantProfileSetup? _FirstRunSetup()
    {
        var pluginProviders = (_resumeState?.Providers ?? [])
            .SelectMany(provider => provider.SessionProviders)
            .DistinctBy(provider => provider.Id)
            .Select(provider => new SessionProviderOption(provider.Name, SessionProvider.Plugin, provider.Id))
            .ToList();
        IReadOnlyList<SessionProviderOption> providers =
        [
            .. SessionProviderCatalog.Providers.Where(option => option.Value is SessionProvider.Ollama or SessionProvider.LmStudio),
            .. pluginProviders,
        ];
        var preferred = pluginProviders.Count == 1 ? pluginProviders[0] : null;

        return new AssistantProfileSetup(providers, preferred, _RememberDeferredProfileAsync);
    }

    private async Task _RememberDeferredProfileAsync(SessionProfile profile, CancellationToken cancellationToken)
    {
        if (_resumeState is not { } state
            || _wizardState is null
            || profile.ProviderConfig is not PluginProviderConfig { ProviderId: { Length: > 0 } } provider)
        {
            return;
        }

        var providerName = state.Providers
            .SelectMany(choice => choice.SessionProviders)
            .First(choice => choice.Id == provider.ProviderId).Name;
        var updated = state with
        {
            AssistantProviderId = provider.ProviderId,
            AssistantProviderName = providerName,
            AssistantConfigJson = string.IsNullOrWhiteSpace(provider.ConfigJson) ? "{}" : provider.ConfigJson,
        };
        await _wizardState.SaveResumeStateAsync(updated, cancellationToken).ConfigureAwait(true);
        _resumeState = updated;
        _rememberDeferred?.Invoke(updated);
        DeferredAssistantName = providerName;
        AssistantOptions.IsEnabled = true;
        NextBlockedChanged?.Invoke(this, EventArgs.Empty);
    }
}
