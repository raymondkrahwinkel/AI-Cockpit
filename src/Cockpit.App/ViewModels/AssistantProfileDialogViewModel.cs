using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.App.ViewModels;

// *Why this exists rather than a picker in Options.* `AssistantProfileSlot` has always held a whole `SessionProfile` of
// its own — never a reference into the profile list — and Options presented it as a selection from that list (AC-410,
// AC-546).
public sealed partial class AssistantProfileDialogViewModel : ViewModelBase
{
    private readonly IAssistantProfileStore? _slotStore;
    private readonly ISessionProfileStore? _sessionProfileStore;
    private readonly IAssistantSessionHost? _assistant;
    private readonly IProfileLoginChecker? _loginChecker;
    private readonly IPluginProviderRegistry? _pluginProviderRegistry;
    private readonly IMcpServerCatalog? _mcpServerCatalog;
    private readonly IMcpToolTokenEstimator? _tokenEstimator;
    private readonly ITtySessionProviderResolver? _ttyProviderResolver;
    private readonly IReadOnlyList<SessionProviderOption> _providers;

    private IReadOnlyList<string> _availableMcpServerNames = [];

    // Raised when the dialog should close (after a save, or on cancel).
    public event Action? CloseRequested;

    // Both of the assistant's own MCP endpoints, named for the hint under the checklist so the UI and the mount rule
    // cannot disagree about which servers arrive whatever is ticked.
    public static string AlwaysMountedMcpServers =>
        $"{AssistantIdentity.McpServerName} and {AssistantIdentity.ActMcpServerName}";

    // Replaced wholesale by `CopyFromCommand`, which is why it is a property rather than a readonly field.
    [ObservableProperty]
    private EditableProfileViewModel _profile;

    // The ordinary session profiles offered as a starting point. Never a live link — see `CopyFromCommand`.
    public ObservableCollection<SessionProfile> CopyFromProfiles { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyFromCommand))]
    private SessionProfile? _copyFromProfile;

    // Whether there is anything to copy in — the whole row is hidden when the cockpit has no other profiles.
    public bool HasCopyFromProfiles => CopyFromProfiles.Count > 0;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    // Whether the standing instruction above replaces the assistant's built-in one instead of adding to it
    // (AC-594). Lives here rather than on the record: an ordinary profile has no built-in instruction to replace.
    [ObservableProperty]
    private bool _replacesStandingInstruction;

    // False in the design-time graph and in a cockpit that never wired a host.
    public bool CanRestartAssistant => _assistant is not null;

    // The plugin option rows, MCP rows and variable are staged directly rather than resolved: a headless render has no
    // plugin registry and no MCP catalog, so a scene built from the real path would show the empty half of every block
    // and prove nothing about the parts this dialog exists for.
    public AssistantProfileDialogViewModel(IAssistantSessionHost? assistant = null, IPluginProviderRegistry? pluginProviderRegistry = null)
    {
        _assistant = assistant;
        _pluginProviderRegistry = pluginProviderRegistry;
        _providers = pluginProviderRegistry is null
            ? SessionProviderCatalog.Providers
            : SessionProviderCatalog.AllProviders(pluginProviderRegistry);

        // Fase 4 only understands a Claude profile as one of these, and the legacy shape resolves to no provider option
        // at all — it falls back to Ollama and renders a local-model form under a label saying Claude, which is exactly
        // what the first render of this scene showed.
        _profile = new EditableProfileViewModel(
            new SessionProfile("Claude (assistant)", ClaudePluginProfile.Create("/home/raymond/.claude", null))
            {
                SystemPrompt = "You are the cockpit's assistant. Answer briefly; you are usually being listened to, not read.",
            },
            isLoggedIn: true,
            canChooseProvider: false,
            _providers,
            pluginProviderRegistry);

        // The session-default rows are not staged here: the editor builds them from the provider's own declaration,
        // so a registry that resolves already produces them and adding a set by hand rendered every one of them
        // twice. Only what no declaration can supply is staged — a catalog of MCP servers and a variable.
        foreach (var server in (string[])["depot", "youtrack", "cockpit-terminal"])
        {
            _profile.McpServers.Add(new McpServerSelectionItemViewModel(server) { IsEnabledForSession = server != "cockpit-terminal" });
        }

        _profile.RestrictMcpServers = true;
        _profile.EnvironmentVariables.Add(new ProfileEnvironmentVariableViewModel("DEPOT_TOKEN", "hunter2", isSecret: true));

        // Two to copy from, so the starting-point row and the sentence under it are on screen. The row hides
        // entirely when the cockpit has no other profiles, which is a state no render would otherwise show.
        CopyFromProfiles.Add(new SessionProfile("work", ClaudePluginProfile.Create("/home/raymond/.claude-work", null)));
        CopyFromProfiles.Add(new SessionProfile("local", new OllamaConfig("http://localhost:11434", "qwen2.5-coder:7b", null)));
    }

    public AssistantProfileDialogViewModel(
        IAssistantProfileStore slotStore,
        ISessionProfileStore sessionProfileStore,
        IAssistantSessionHost? assistant = null,
        IProfileLoginChecker? loginChecker = null,
        IPluginProviderRegistry? pluginProviderRegistry = null,
        IMcpServerCatalog? mcpServerCatalog = null,
        IMcpToolTokenEstimator? tokenEstimator = null,
        ITtySessionProviderResolver? ttyProviderResolver = null)
    {
        _slotStore = slotStore;
        _sessionProfileStore = sessionProfileStore;
        _assistant = assistant;
        _loginChecker = loginChecker;
        _pluginProviderRegistry = pluginProviderRegistry;
        _mcpServerCatalog = mcpServerCatalog;
        _tokenEstimator = tokenEstimator;
        _ttyProviderResolver = ttyProviderResolver;
        _providers = pluginProviderRegistry is null
            ? SessionProviderCatalog.Providers
            : SessionProviderCatalog.AllProviders(pluginProviderRegistry);

        // Replaced by LoadAsync; assigned here so the field is never null between construction and the load.
        _profile = _Editable(new SessionProfile(AssistantProfileSlot.DisplayName, new ClaudeConfig(string.Empty)));
    }

    // Reads the slot, the MCP catalog and the profiles that can be copied in.
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_mcpServerCatalog is not null)
        {
            var servers = await _mcpServerCatalog.GetServersAsync(cancellationToken).ConfigureAwait(true);
            _availableMcpServerNames = [.. McpServerRegistryFilter.OfferedToOperator(servers).Select(server => server.Name)];
        }

        if (_sessionProfileStore is not null)
        {
            CopyFromProfiles.Clear();
            foreach (var profile in await _sessionProfileStore.LoadAsync(cancellationToken).ConfigureAwait(true))
            {
                CopyFromProfiles.Add(profile);
            }

            OnPropertyChanged(nameof(HasCopyFromProfiles));
        }

        if (_slotStore is not null)
        {
            var slot = await _slotStore.LoadAsync(cancellationToken).ConfigureAwait(true);

            // A slot that has never been set up opens on a blank Claude-shaped record rather than refusing to open:
            // this dialog is where an assistant profile comes into being, so "there is none yet" is its empty state,
            // not an error. Its own display name is the starting label — the assistant is what it is for.
            Profile = _Editable(slot.Profile ?? new SessionProfile(AssistantProfileSlot.DisplayName, new ClaudeConfig(string.Empty)));
            ReplacesStandingInstruction = slot.ReplacesStandingInstruction;
            StatusMessage = slot.Profile is null ? slot.UnsetReason ?? string.Empty : string.Empty;
        }
    }

    // Fills this dialog in from an ordinary profile — a starting point, not a link.
    [RelayCommand(CanExecute = nameof(CanCopyFrom))]
    private void CopyFrom()
    {
        if (CopyFromProfile is not { } source)
        {
            return;
        }

        // The label deliberately comes across too: a copied profile that kept the previous name would leave the
        // dialog claiming to be one thing and running another.
        Profile = _Editable(source);
        StatusMessage = $"Copied the settings from \"{source.Label}\". They belong to the assistant now — editing \"{source.Label}\" later changes nothing here.";
    }

    private bool CanCopyFrom => CopyFromProfile is not null;

    // Persists the edits onto the slot and closes.
    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (await _PersistAsync(cancellationToken).ConfigureAwait(true))
        {
            CloseRequested?.Invoke();
        }
    }

    // Persists the edits and restarts the assistant on them, without closing (#15).
    [RelayCommand]
    private async Task SaveAndRestartAsync(CancellationToken cancellationToken)
    {
        if (!await _PersistAsync(cancellationToken).ConfigureAwait(true) || _assistant is null)
        {
            return;
        }

        StatusMessage = "Restarting the assistant…";
        await _assistant.RestartAsync(cancellationToken).ConfigureAwait(true);
        StatusMessage = "Saved, and the assistant restarted on these settings — same conversation.";
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    // Writes the slot, or says why it could not. Returns whether anything was persisted.
    private async Task<bool> _PersistAsync(CancellationToken cancellationToken)
    {
        if (!Profile.IsValid)
        {
            StatusMessage = "This profile is missing something its provider needs — check the label and the provider settings.";
            return false;
        }

        if (_slotStore is null)
        {
            return false;
        }

        // That is what keeps SessionProfile's own "a record never changes provider" invariant true while the assistant
        // stays switchable between Claude, Codex and a local model (AC-543).
        await _slotStore.RepointAsync(Profile.ToProfile(), ReplacesStandingInstruction, cancellationToken).ConfigureAwait(true);
        StatusMessage = "Saved.";
        return true;
    }

    private EditableProfileViewModel _Editable(SessionProfile profile) => new(
        profile,
        _loginChecker?.IsLoggedIn(profile) ?? true,
        // Unlike the profile list, where a provider is fixed after creation so credentials can never describe a backend
        // the profile no longer talks to (AC-543).
        canChooseProvider: true,
        _providers,
        _pluginProviderRegistry,
        _availableMcpServerNames,
        _tokenEstimator,
        _ttyProviderResolver);
}
