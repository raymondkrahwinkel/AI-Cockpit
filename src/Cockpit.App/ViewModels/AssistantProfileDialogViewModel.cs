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

/// <summary>
/// The assistant's own profile, edited in its own small dialog.
/// </summary>
/// <remarks>
/// <b>Why this exists rather than a picker in Options.</b> <see cref="AssistantProfileSlot"/> has always held a whole
/// <see cref="SessionProfile"/> of its own — never a reference into the profile list — and Options presented it as a
/// selection from that list. So an operator edited "default" in Manage profiles, saw the assistant still pointing at
/// "default", and was still asked to confirm every tool call, because the assistant's record was a different object
/// that had been copied from it once. Every attempt to patch that (say so on the page, offer a refresh button) is an
/// annotation on a belief the UI itself was creating. Naming the record what it is — the assistant's profile, edited
/// here — removes the belief instead: there is nothing left to drift from, no id to match on, and AC-410 stops
/// applying altogether because nothing outside this dialog can rename or delete this record.
/// <para>
/// <b>What it shows, and why it is short.</b> Base settings, the provider and its own settings, the session defaults,
/// the MCP pre-selection and the environment variables — the five blocks the operator named. Everything the ordinary
/// profile editor carries beyond those was checked against what
/// <c>AssistantSessionHost.EnsureStartedAsync</c> actually consumes and is unreachable for this record: delegation is
/// read by <c>DelegationService</c> and <c>list_profiles</c> off <see cref="ISessionProfileStore"/>, which this record
/// is deliberately not in; the session kind is read by <c>SessionKindDefaults</c> for the New-session dialog, while
/// the assistant is always minted by <c>CockpitViewModel.CreateAssistantSession</c> as an SDK session; the default
/// working directory is read by <c>SessionStartDefaults</c>, and the assistant's start passes no working directory at
/// all; the default reading level would be read by <c>StartConfiguredAsync</c> only if the host passed none, and it
/// always passes <c>AssistantSettings.ReadingLevel</c> (AC-546), which is set on the Options page this dialog opens
/// from. A field that reaches nothing is a control that lies.
/// </para>
/// <para>
/// <b>Nothing it hides can be lost.</b> The edits run through <see cref="EditableProfileViewModel"/>, which is seeded
/// from the stored record and rebuilds it whole — so a field this dialog does not render is carried across untouched
/// rather than defaulted away on save. That is the failure this repo has had twice (<c>ProjectDialogViewModel</c>),
/// and the reason the editor is reused rather than a second, smaller one written beside it.
/// </para>
/// </remarks>
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

    /// <summary>Raised when the dialog should close (after a save, or on cancel).</summary>
    public event Action? CloseRequested;

    /// <summary>Both of the assistant's own MCP endpoints, named for the hint under the checklist so the UI and the mount rule cannot disagree about which servers arrive whatever is ticked.</summary>
    public static string AlwaysMountedMcpServers =>
        $"{AssistantIdentity.McpServerName} and {AssistantIdentity.ActMcpServerName}";

    /// <summary>The record being edited. Replaced wholesale by <see cref="CopyFromCommand"/>, which is why it is a property rather than a readonly field.</summary>
    [ObservableProperty]
    private EditableProfileViewModel _profile;

    /// <summary>The ordinary session profiles offered as a starting point. Never a live link — see <see cref="CopyFromCommand"/>.</summary>
    public ObservableCollection<SessionProfile> CopyFromProfiles { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyFromCommand))]
    private SessionProfile? _copyFromProfile;

    /// <summary>Whether there is anything to copy in — the whole row is hidden when the cockpit has no other profiles.</summary>
    public bool HasCopyFromProfiles => CopyFromProfiles.Count > 0;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>Whether a running assistant can be restarted from here. False in the design-time graph and in a cockpit that never wired a host.</summary>
    public bool CanRestartAssistant => _assistant is not null;

    /// <summary>
    /// Design-time, the previewer and the screenshot harness: a record shaped like the one an operator actually has,
    /// filled in far enough that every block of this dialog is on screen at once.
    /// </summary>
    /// <remarks>
    /// The plugin option rows, MCP rows and variable are staged directly rather than resolved: a headless render has
    /// no plugin registry and no MCP catalog, so a scene built from the real path would show the empty half of every
    /// block and prove nothing about the parts this dialog exists for. <paramref name="assistant"/> is what puts the
    /// restart box on screen — it is hidden without a living assistant, and a scene that could not show it would
    /// leave the one control this whole round moved unverifiable.
    /// </remarks>
    public AssistantProfileDialogViewModel(IAssistantSessionHost? assistant = null, IPluginProviderRegistry? pluginProviderRegistry = null)
    {
        _assistant = assistant;
        _pluginProviderRegistry = pluginProviderRegistry;
        _providers = pluginProviderRegistry is null
            ? SessionProviderCatalog.Providers
            : SessionProviderCatalog.AllProviders(pluginProviderRegistry);

        // The bundled Claude provider plugin's config, not a bare ClaudeConfig. Fase 4 only understands a Claude
        // profile as one of these, and the legacy shape resolves to no provider option at all — it falls back to
        // Ollama and renders a local-model form under a label saying Claude, which is exactly what the first render
        // of this scene showed. Same trap the Manage-profiles scenes already document.
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

    /// <summary>Reads the slot, the MCP catalog and the profiles that can be copied in.</summary>
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
            StatusMessage = slot.Profile is null ? slot.UnsetReason ?? string.Empty : string.Empty;
        }
    }

    /// <summary>
    /// Fills this dialog in from an ordinary profile — a starting point, not a link.
    /// </summary>
    /// <remarks>
    /// One-time by construction: the values are read into the editor here and nothing records where they came from,
    /// so there is no later moment at which the two could agree or disagree. That is the whole point of the shape —
    /// the previous design kept a copy and had to explain, on the page, that it was one.
    /// </remarks>
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

    /// <summary>Persists the edits onto the slot and closes.</summary>
    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (await _PersistAsync(cancellationToken).ConfigureAwait(true))
        {
            CloseRequested?.Invoke();
        }
    }

    /// <summary>
    /// Persists the edits and restarts the assistant on them, without closing.
    /// </summary>
    /// <remarks>
    /// <b>The button belongs to the settings above it, not to a window.</b> A permission mode is read at a launch and,
    /// for <c>bypassPermissions</c>, can only be chosen at one — <c>SessionOptionCatalog</c> keeps
    /// <c>LivePermissionModes</c> apart from <c>AllPermissionModes</c> for exactly that reason (bug #15). Saying "this
    /// applies at the next start" is only useful next to the thing that provides one; it lived in the chat window's
    /// header for a while, which meant changing a setting here and then going to another window to make it count.
    /// <para>
    /// Deliberately does not close: a restart is something you watch the result of, and the settings that caused it
    /// should still be on screen. The conversation is picked up again by the host, so this costs nothing but the wait.
    /// </para>
    /// </remarks>
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

    /// <summary>Writes the slot, or says why it could not. Returns whether anything was persisted.</summary>
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

        // RepointAsync takes a whole record and can express nothing else, so every save here mints a new
        // SessionProfile and hands it over — including a save that changed the provider. That is what keeps
        // SessionProfile's own "a record never changes provider" invariant true while the assistant stays
        // switchable between Claude, Codex and a local model (AC-543).
        await _slotStore.RepointAsync(Profile.ToProfile(), cancellationToken).ConfigureAwait(true);
        StatusMessage = "Saved.";
        return true;
    }

    private EditableProfileViewModel _Editable(SessionProfile profile) => new(
        profile,
        _loginChecker?.IsLoggedIn(profile) ?? true,
        // Unlike the profile list, where a provider is fixed after creation so credentials can never describe a
        // backend the profile no longer talks to. There is one record here and it is replaced whole on every save,
        // so a provider switch mints a new record rather than mutating one — the very operation AC-543 says the
        // assistant must support. Fixing it would mean the only way to move the assistant to Codex is to build a
        // profile elsewhere and copy it in, which is a detour with no invariant behind it.
        canChooseProvider: true,
        _providers,
        _pluginProviderRegistry,
        _availableMcpServerNames,
        _tokenEstimator,
        _ttyProviderResolver);
}
