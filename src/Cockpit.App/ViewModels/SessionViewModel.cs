using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Plugins;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Profiles;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.ViewModels;

/// <summary>
/// F-C1 cockpit: a single Claude Code session rendered as a streaming transcript with a
/// chat-style input box and read-only-so-far allow/deny affordances for tool use.
/// </summary>
/// <remarks>
/// Visual layout has not been verified against a running Avalonia window in this sandbox
/// (no display available here) — treat the XAML as unverified until Raymond runs it.
/// </remarks>
public partial class SessionViewModel : SessionPanelViewModel, ITransientService
{
    private readonly ISessionManager? _sessionManager;

    // Optional (null at design time / in tests): the host routes a message's images to any plugin that registered
    // a sink, so a tracker can attach them to the issue this session tracks (AC-14).
    private readonly ISessionImageSinkRegistry? _imageSinkRegistry;

    // The session itself — driver, event pump, lifetime — lives in the runtime (#68); this panel is one of its
    // consumers, not its owner. Created once the profile (and therefore the provider) is known, in
    // StartWithProfileAsync. The manager owns it and is the one place it gets stopped.
    private ISessionRuntime? _runtime;

    /// <summary>The per-session MCP-server selection (#44) from the New-session dialog, set just before <see cref="StartWithProfileAsync"/> reads it in <see cref="StartConfiguredAsync"/>.</summary>
    private IReadOnlySet<string>? _enabledMcpServerNames;

    /// <summary>The per-session plugin-provider launch options (sandbox, model) from the New-session dialog, set the same way as <see cref="_enabledMcpServerNames"/> just before <see cref="StartWithProfileAsync"/> reads them.</summary>
    private IReadOnlyDictionary<string, string>? _launchOptions;
    private TranscriptEntryViewModel? _currentAssistantEntry;
    private TranscriptEntryViewModel? _currentThinkingEntry;

    /// <summary>Assistant-text rows added since the last <see cref="TurnCompleted"/> — a turn can produce several (text, tool call, more text), so the read-aloud trigger (#35) reads all of them, not just the last.</summary>
    private readonly List<TranscriptEntryViewModel> _currentTurnAssistantEntries = [];

    /// <summary>Set when an "exit" message is dispatched with auto-close on, so the next completed turn closes the session (T10).</summary>
    private bool _closeAfterTurn;

    public ObservableCollection<TranscriptEntryViewModel> Transcript { get; } = [];

    /// <summary>False until the first transcript row arrives, so the panel can show a calm empty-state hint instead of a void.</summary>
    public bool HasTranscript => Transcript.Count > 0;

    /// <summary>Images pasted into the input, sent with the next message and cleared afterwards.</summary>
    public ObservableCollection<ImageAttachmentViewModel> PendingAttachments { get; } = [];

    /// <summary>True while at least one image is queued, so the chip strip can hide when empty.</summary>
    public bool HasPendingAttachments => PendingAttachments.Count > 0;

    /// <summary>
    /// True when this session's driver actually sends pasted images to the model (#64) — gates
    /// <see cref="AddPastedImage"/> so a provider without <see cref="SessionCapabilities.SupportsVision"/>
    /// (Ollama/LM Studio, the current plugin providers) never silently drops a pasted image. Notified
    /// alongside <see cref="SessionPanelViewModel.Capabilities"/> in <see cref="StartWithProfileAsync"/>,
    /// the one place that property changes after the driver starts.
    /// </summary>
    public bool CanPasteImages => Capabilities is { SupportsVision: true };

    /// <summary>Messages typed while a turn was in flight, dispatched in order as turns complete (T8).</summary>
    public ObservableCollection<QueuedMessageViewModel> QueuedMessages { get; } = [];

    /// <summary>True while the send queue holds a message, so the queued-chip strip can hide when empty.</summary>
    public bool HasQueuedMessages => QueuedMessages.Count > 0;

    /// <summary>
    /// True when there is text or an image to act on, so Send is enabled exactly when it will do
    /// something. It does not gate on <see cref="IsBusy"/>: while a turn runs, Send queues the message
    /// (T8) rather than being disabled, so you can keep typing ahead without losing input.
    /// </summary>
    public bool CanSend => !string.IsNullOrWhiteSpace(InputText) || PendingAttachments.Count > 0;

    /// <summary>
    /// Permission modes offered in the running panel: the three live-switchable modes
    /// (<see cref="SessionOptionCatalog.LivePermissionModes"/>), or — once a session was launched in
    /// bypass — a single locked "Bypass permissions" entry, since the CLI cannot switch a running
    /// session into or out of bypass. The dialog offers the full four via the catalog.
    /// </summary>
    public IReadOnlyList<PermissionModeOption> PermissionModes =>
        IsPermissionModeLocked ? [SelectedPermissionMode] : SessionOptionCatalog.LivePermissionModes;

    [ObservableProperty]
    private PermissionModeOption _selectedPermissionMode = SessionOptionCatalog.DefaultPermissionMode;

    /// <summary>
    /// True once the session was launched in bypass: bypass is terminal (launch-only), so the panel
    /// dropdown is disabled rather than offering a switch the CLI would reject — no dead control (#15).
    /// </summary>
    [ObservableProperty]
    private bool _isPermissionModeLocked;

    partial void OnIsPermissionModeLockedChanged(bool value) => OnPropertyChanged(nameof(PermissionModes));

    /// <summary>The Claude model aliases suggested in the editable model field; the field stays free text so a specific model or snapshot can be pinned live, matching the New-session dialog.</summary>
    public IReadOnlyList<string> ClaudeModelSuggestions => SessionOptionCatalog.ClaudeModelSuggestions;

    /// <summary>
    /// The running session's model of record: the launch <c>--model</c>, and what a live switch updates. The header
    /// edits it through <see cref="LiveModelText"/> rather than binding here directly, so a switch applies on commit
    /// (Enter/focus-loss) instead of on every keystroke.
    /// </summary>
    [ObservableProperty]
    private ModelOption _selectedModel = SessionOptionCatalog.DefaultModel;

    /// <summary>
    /// The editable text in the header's Claude model field. Setting it has no side effect — the live switch fires
    /// only when <see cref="CommitLiveModel"/> is called (the view commits on Enter, focus-loss, or picking a
    /// suggestion), so typing a snapshot name does not fire a set_model control request per character.
    /// </summary>
    [ObservableProperty]
    private string _liveModelText = SessionOptionCatalog.DefaultModel.Value;

    /// <summary>Thinking-effort levels offered per session; drives the thinking-budget control.</summary>
    public IReadOnlyList<EffortOption> Efforts => SessionOptionCatalog.Efforts;

    [ObservableProperty]
    private EffortOption _selectedEffort = SessionOptionCatalog.DefaultEffort;

    /// <summary>
    /// The running plugin provider's generic live controls (#45 D4) — Codex's model and effort — populated after
    /// start from the driver's declared options. Empty for Claude and local sessions, which drive their controls
    /// through the typed dropdowns above; a provider with nothing to switch leaves the panel hidden.
    /// </summary>
    public ObservableCollection<LiveControlViewModel> LiveControls { get; } = [];

    /// <summary>True once the running provider declared at least one generic live control, so the panel shows only when it has something in it.</summary>
    public bool HasLiveControls => LiveControls.Count > 0;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string _status = "Not started.";

    /// <summary>
    /// What the status line says on hover about the session's tools: the count, or why there are none. The names
    /// themselves are <see cref="ConnectedTools"/> — fifty-five of them run together with commas was a wall of text,
    /// and a list nobody can read is a list nobody checks.
    /// </summary>
    [ObservableProperty]
    private string _connectedToolsHeading = string.Empty;

    /// <summary>The session's connected tool names, so it is verifiable which tools (file tools, say) the model actually has.</summary>
    public ObservableCollection<string> ConnectedTools { get; } = [];

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// True from the moment a turn is sent until the assistant produces its first sign of activity (a text
    /// or thinking delta, or a tool call). Drives the "Thinking…" indicator so a local model — which has no
    /// streaming thinking and can sit silent while it loads/processes the prompt — visibly shows it is working
    /// rather than looking hung.
    /// </summary>
    [ObservableProperty]
    private bool _isAwaitingResponse;

    /// <summary>Shows the "Allow all tools" toggle: a local tool session (has tools, but not Claude's own permission modes) whose every MCP call would otherwise need an Allow click.</summary>
    [ObservableProperty]
    private bool _showToolAutoApprove;

    /// <summary>When on, this session runs tool calls without prompting (still shown as tool rows). Applied live to the driver.</summary>
    [ObservableProperty]
    private bool _autoApproveTools;

    /// <summary>True while a pending permission decision or CLI <c>needs_action</c> signal is outstanding, driving <see cref="SessionStatus.NeedsAttention"/>.</summary>
    private bool _needsAttention;

    /// <summary>True once at least one turn has finished, so an idle session reads as Done rather than Idle — independent of whether a (success) turn added a transcript row (T4).</summary>
    private bool _hasCompletedATurn;

    /// <summary>Running token/cost total for the session (#8), folded from each completed turn's result usage.</summary>
    private readonly SessionUsageMeter _usage = new();

    /// <summary>True once any usage/cost has accrued, so the meter shows only when there is something to show.</summary>
    [ObservableProperty]
    private bool _hasUsage;

    /// <summary>Compact token/cost meter shown next to the status, e.g. "45.2k tok · $0.0123" (#8).</summary>
    [ObservableProperty]
    private string _usageSummary = string.Empty;

    /// <summary>Per-bucket usage breakdown for the meter's hover text (#8).</summary>
    [ObservableProperty]
    private string _usageTooltip = string.Empty;

    /// <summary>
    /// How full the context window is (#45 D7), drawn as the header's "ctx" bar. Null until the provider reports
    /// it — a bar reading "0%" would be a claim rather than a silence. Fed from the driver's status feed (Codex's
    /// app-server usage); a provider with no feed leaves it null and the bar stays hidden.
    /// </summary>
    [ObservableProperty]
    private double? _contextUsedPercent;

    /// <summary>
    /// The provider's usage windows, each a self-labelled header bar (#45 D7) — the provider chooses the label
    /// ("5h", "wk", …), so the header renders whatever it reports without baking in window vocabulary. Empty when
    /// the provider reports none.
    /// </summary>
    public ObservableCollection<SessionRateWindow> RateLimits { get; } = [];

    /// <summary>The whole story on hover, including when each window rolls over — the thing a bar cannot say.</summary>
    [ObservableProperty]
    private string _limitsTooltip = string.Empty;

    // Parameterless constructor kept for the Avalonia previewer design-time context. Seeds a
    // few sample transcript rows so the previewer/Screenshotter render the styled components
    // (thinking, tool-use, collapsed tool-result, pending permission) — does not touch the real
    // DI-backed session.
    public SessionViewModel()
    {
        Status = "Connected (12 tools, cwd=D:/Projects/dotnet/Cockpit).";
        ActiveProfileLabel = "raymond@work";

        // Sample status bars (#45 D7) so the previewer/Screenshotter renders the header's ctx bar and the
        // provider-labelled window bars.
        ContextUsedPercent = 37;
        RateLimits.Add(new SessionRateWindow("5h", 58, null));
        RateLimits.Add(new SessionRateWindow("wk", 82, null));
        LimitsTooltip = "Context window: 37% used";

        // Sample generic live controls (#45 D4) so the previewer/Screenshotter renders the header's live-control panel.
        // Provider-neutral placeholder values on purpose: the core names no provider's models, not even in sample data.
        LiveControls.Add(new LiveControlViewModel(new SessionLiveOption("model", "Model", ["model-large", "model-fast"], "model-large"), (_, _) => Task.CompletedTask));
        LiveControls.Add(new LiveControlViewModel(new SessionLiveOption("effort", "Effort", ["low", "medium", "high"], "medium"), (_, _) => Task.CompletedTask));

        Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "fix the layout bug in SessionView"));

        var thinking = new TranscriptEntryViewModel(TranscriptEntryKind.Thinking,
            "The user wants the layout bug fixed. Let me look at the XAML structure first...")
        {
            IsExpanded = false,
        };
        Transcript.Add(thinking);

        // Markdown-rich sample so the previewer/Screenshotter exercise the markdown path (T9):
        // heading, bold, inline code, a fenced code block, and a list.
        Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText,
            "## Wat er is\n\n" +
            "- `release.yml` builds **only the desktop client** and attaches it to the release.\n" +
            "- There is a `Dockerfile` but **no workflow** pushing the server image.\n\n" +
            "```csharp\nDockPanel.SetDock(topBar, Dock.Top);\n```\n\n" +
            "| Repo | History | Status |\n|------|---------|--------|\n" +
            "| Playground-RK *(private)* | full dev history, `498` commits | your work repo |\n" +
            "| EveTogether *(public)* | squashed base, `3` commits | official repo |\n\n" +
            "More on the [metadata-action](https://github.com/docker/metadata-action) (clickable)."));

        var editTool = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse,
            "Tool: Edit({\"file_path\":\"SessionView.axaml\",\"old_string\":\"...\"})")
        {
            ToolUseId = "sample-tool-1",
            ToolName = "Edit",
            InputJson = "{\"file_path\":\"SessionView.axaml\",\"old_string\":\"...\"}",
            IsExpanded = true,
        };
        editTool.SetResult("{\"success\":true,\"file\":\"SessionView.axaml\",\"changesApplied\":3,\"warnings\":[]}", isError: false);
        Transcript.Add(editTool);

        Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse,
            "Tool: Bash({\"command\":\"dotnet build\"})")
        {
            ToolUseId = "sample-tool-2",
            ToolName = "Bash",
            InputJson = "{\"command\":\"dotnet build\"}",
            IsPendingPermission = true,
        });


        _TrackPendingAttachments();

        // A sample queued message so the previewer/Screenshotter render the send-queue strip (T8).
        QueuedMessages.Add(new QueuedMessageViewModel(
            "run the tests once the build finishes", [], m => QueuedMessages.Remove(m)));
    }

    public SessionViewModel(
        ISessionManager sessionManager,
        IVoicePushToTalkService? voicePushToTalk = null,
        IVoiceSettingsStore? voiceSettingsStore = null,
        IVoicePlaybackQueue? voicePlaybackQueue = null,
        ITranscriptCleanupService? cleanupService = null,
        ISessionImageSinkRegistry? imageSinkRegistry = null)
    {
        _sessionManager = sessionManager;
        _imageSinkRegistry = imageSinkRegistry;
        _TrackPendingAttachments();
        InitializeVoice(voicePushToTalk, voiceSettingsStore, voicePlaybackQueue, cleanupService);
    }

    private void _TrackPendingAttachments()
    {
        PendingAttachments.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPendingAttachments));
            OnPropertyChanged(nameof(CanSend));
        };
        Transcript.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTranscript));
        QueuedMessages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasQueuedMessages));
        LiveControls.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLiveControls));
    }

    /// <summary>Keeps the Send button's enabled state in sync as the input text changes (T8 CanSend).</summary>
    partial void OnInputTextChanged(string value) => OnPropertyChanged(nameof(CanSend));

    /// <summary>
    /// Starts the session immediately under the profile and options chosen up front in the New-session
    /// dialog (#31) — this replaces the old in-panel Start button and inline profile picker. When
    /// launched in bypass the panel mode dropdown locks, since bypass cannot be switched into or out of
    /// on a running session (#15).
    /// </summary>
    public async Task StartConfiguredAsync(SessionProfile profile, PermissionModeOption mode, ModelOption model, EffortOption effort, IReadOnlySet<string>? enabledMcpServerNames = null, string? workingDirectory = null, SessionResume? resume = null, IReadOnlyDictionary<string, string>? launchOptions = null)
    {
        if (_runtime is not null)
        {
            return;
        }

        // Set the live selectors before starting: the session has no event loop yet, so these do not
        // fire a live control request — they are the launch values StartWithProfileAsync reads. For
        // bypass, lock immediately (right after selecting it) so the dropdown shows the single locked
        // "Bypass permissions" entry without a frame where the selection sits outside the bound list.
        var isBypass = mode.Value == SessionOptionCatalog.BypassPermissionModeValue;
        SelectedPermissionMode = mode;
        IsPermissionModeLocked = isBypass;
        SelectedModel = model;
        LiveModelText = model.Value;
        SelectedEffort = effort;
        _enabledMcpServerNames = enabledMcpServerNames;

        // AC-13: hand the provider this session's own pane id, which its plugin turns into COCKPIT_PANE_ID in the
        // child's environment, so the agent can name its own session to the cockpit-session MCP's set_status tool.
        var mergedOptions = launchOptions is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(launchOptions, StringComparer.OrdinalIgnoreCase);
        mergedOptions[WellKnownPluginSessionOptions.PaneId] = PaneId;
        _launchOptions = mergedOptions;

        await StartWithProfileAsync(profile, workingDirectory, resume);

        // StartWithProfileAsync swallows launch failures (it only sets Status); the runtime is left un-started
        // when the CLI never came up. In that case unlock and reset the mode so a failed bypass launch doesn't
        // strand the panel on a phantom, disabled "Bypass permissions" with no session.
        if (_runtime is not { IsRunning: true })
        {
            IsPermissionModeLocked = false;
            SelectedPermissionMode = SessionOptionCatalog.DefaultPermissionMode;
        }
    }

    private async Task StartWithProfileAsync(SessionProfile? profile, string? workingDirectory = null, SessionResume? resume = null)
    {
        if (_sessionManager is null)
        {
            return;
        }

        ProviderBadge = profile?.Provider is null or SessionProvider.ClaudeCli
            ? string.Empty
            : SessionProviderCatalog.Resolve(profile.Provider).Label;

        // A per-session working directory override reflects immediately on the shared base (so the header and
        // the read/observe surface show where this session runs) even before the CLI's own init event confirms
        // its cwd; a blank override leaves it to be filled from that init event as before.
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            WorkingDirectory = workingDirectory;
        }

        Status = "Starting...";

        try
        {
            // The runtime owns the driver and the event pump (#68); this panel subscribes to its events and
            // marshals them onto the UI thread itself. Inside the try: a profile referencing a missing or
            // unresolvable plugin provider (or an invalid persisted ConfigJson) throws during the runtime's
            // start — catching it degrades to the existing failed-launch path (Status set, no running runtime)
            // instead of an unhandled throw stranding the panel that CockpitViewModel already added.
            var runtime = _sessionManager.Create(profile);
            runtime.EventAppended += _OnSessionEvent;
            _runtime = runtime;

            // The model dropdown lists Claude aliases (opus/sonnet/…), which are meaningless to a local
            // provider — it uses the model set on its profile. Only pass the selected model for Claude, so
            // a local session keeps its own configured model instead of being clobbered with "opus".
            var launchModel = profile?.Provider is null or SessionProvider.ClaudeCli ? SelectedModel.Value : null;
            await runtime.StartAsync(profile, SelectedPermissionMode.Value, launchModel, _enabledMcpServerNames, workingDirectory, resume, _launchOptions);

            // The process the meter weighs (#78) exists only once the driver started it.
            ProcessId = runtime.ProcessId;

            // Capabilities (notably SupportsTools) only settle once the driver has actually started — the
            // local (OpenAI-compatible) driver's SupportsTools flips true only after its MCP tool session
            // connects during StartAsync — so read them here rather than right after Create(), which would
            // always see the driver's pre-start (all-false) defaults.
            // The runtime only knows them once its driver is up, which it now is.
            if (runtime.Capabilities is { } capabilities)
            {
                Capabilities = capabilities;
            }

            // The provider's generic live controls (#45 D4) settle at the same moment as capabilities — the driver
            // lists them once its session is up (Codex resolves its model list on start) — so read them here too.
            _PopulateLiveControls();

            OnPropertyChanged(nameof(CanPasteImages));
            // A local tool session gates via the per-call approval prompt (not Claude's permission modes), so it
            // gets the "Allow all tools" convenience toggle; Claude uses its own permission mode dropdown.
            var isLocalToolSession = Capabilities is { SupportsTools: true, SupportsPermissions: false };
            ShowToolAutoApprove = isLocalToolSession;

            // A profile marked "auto-approve tools" (#26) seeds the toggle for a fresh local tool session, so
            // it starts already on instead of needing the operator to flip it every time for a profile they
            // trust. wasAlreadyOn distinguishes that from a choice the operator flipped before the session
            // finished starting: assigning the property below only calls the driver (through
            // OnAutoApproveToolsChanged) when the value actually changes, i.e. exactly the freshly-seeded
            // case — the pre-set case needs its own explicit re-apply just after, since any hook call at
            // flip-time hit a session that wasn't running yet.
            var wasAlreadyOn = AutoApproveTools;
            AutoApproveTools = AutoApproveTools || (isLocalToolSession && profile?.Defaults?.AutoApproveTools == true);

            if (AutoApproveTools && wasAlreadyOn)
            {
                await runtime.SetAutoApproveToolsAsync(true);
            }

            ActiveProfileLabel = profile?.Label;
            // The profile is shown separately (ActiveProfileLabel), so keep the status itself clean
            // rather than repeating it — "Session started. · personal" read as a duplicate (L6).
            Status = "Session started.";

            // Thinking budget has no launch flag — the control request is the only path — so apply
            // the selected effort once the session is live, otherwise it runs at the CLI default
            // until the operator first touches the dropdown.
            await _SetMaxThinkingTokensSafeAsync(SelectedEffort.MaxThinkingTokens);
        }
        catch (Exception ex)
        {
            Status = $"Failed to start: {ex.Message}";
        }
    }

    /// <summary>Live-toggles auto-approval of tool calls on the running session's driver (local sessions).</summary>
    partial void OnAutoApproveToolsChanged(bool value)
    {
        _ = _runtime?.SetAutoApproveToolsAsync(value);
    }

    /// <summary>Live-switches the running session's permission mode. No-op before the session has started.</summary>
    partial void OnSelectedPermissionModeChanged(PermissionModeOption value)
    {
        if (_runtime is not { IsRunning: true })
        {
            return;
        }

        _ = _SetPermissionModeSafeAsync(value.Value);
    }

    /// <summary>Live-switches the running session's model. No-op before the session has started.</summary>
    partial void OnSelectedModelChanged(ModelOption value)
    {
        if (_runtime is not { IsRunning: true })
        {
            return;
        }

        _ = _SetModelSafeAsync(value.Value);
    }

    /// <summary>
    /// Applies the edited Claude model as a live switch, called by the view when the model field commits (Enter,
    /// focus-loss, or picking a suggestion). Routes through <see cref="SelectedModel"/> so the model of record and
    /// the live control request (via <see cref="OnSelectedModelChanged"/>) stay one path; a blank field or an
    /// unchanged value is ignored so a commit that changed nothing fires no request.
    /// </summary>
    public void CommitLiveModel()
    {
        var text = LiveModelText?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var model = SessionOptionCatalog.ModelForValue(text);
        if (model.Value != SelectedModel.Value)
        {
            SelectedModel = model;
        }
    }

    /// <summary>Live-switches the running session's thinking budget. No-op before the session has started.</summary>
    partial void OnSelectedEffortChanged(EffortOption value)
    {
        if (_runtime is not { IsRunning: true })
        {
            return;
        }

        _ = _SetMaxThinkingTokensSafeAsync(value.MaxThinkingTokens);
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (_runtime is not { IsRunning: true })
        {
            return;
        }

        try
        {
            await _runtime.InterruptAsync();
            Status = "Interrupted.";
        }
        catch (Exception ex)
        {
            Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, $"Interrupt failed: {ex.Message}"));
        }
    }

    private async Task _SetPermissionModeSafeAsync(string mode)
    {
        if (_runtime is null)
        {
            return;
        }

        try
        {
            await _runtime.SetPermissionModeAsync(mode);
        }
        catch (Exception ex)
        {
            Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, $"Permission-mode switch failed: {ex.Message}"));
        }
    }

    private async Task _SetModelSafeAsync(string model)
    {
        if (_runtime is null)
        {
            return;
        }

        try
        {
            await _runtime.SetModelAsync(model);
        }
        catch (Exception ex)
        {
            Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, $"Model switch failed: {ex.Message}"));
        }
    }

    private async Task _SetMaxThinkingTokensSafeAsync(int maxThinkingTokens)
    {
        if (_runtime is null)
        {
            return;
        }

        try
        {
            await _runtime.SetMaxThinkingTokensAsync(maxThinkingTokens);
        }
        catch (Exception ex)
        {
            Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, $"Effort switch failed: {ex.Message}"));
        }
    }

    /// <summary>Rebuilds the generic live-control panel from the running driver's declared options (#45 D4).</summary>
    private void _PopulateLiveControls()
    {
        LiveControls.Clear();
        if (_runtime is null)
        {
            return;
        }

        foreach (var option in _runtime.LiveOptions)
        {
            LiveControls.Add(new LiveControlViewModel(option, _SetLiveOptionSafeAsync));
        }
    }

    /// <summary>Live-switches one of the provider's generic controls on the running session's driver (#45 D4).</summary>
    private async Task _SetLiveOptionSafeAsync(string key, string value)
    {
        if (_runtime is null)
        {
            return;
        }

        try
        {
            await _runtime.SetLiveOptionAsync(key, value);
        }
        catch (Exception ex)
        {
            Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, $"Live switch failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Queues a pasted image as a pending attachment for the next message. Called from the view's
    /// CTRL+V handler, which owns the Avalonia clipboard read; the view model only sees PNG bytes so
    /// it stays free of UI-toolkit types and unit-testable.
    /// </summary>
    /// <remarks>
    /// Gated on <see cref="CanPasteImages"/> (#64): the CTRL+V gesture has no button to hide, so a session
    /// whose driver would otherwise silently drop the image (<see cref="SessionCapabilities.SupportsVision"/>
    /// false — today's Ollama/LM Studio/plugin sessions) gets a transcript notice instead of a queued
    /// attachment that vanishes unsent.
    /// </remarks>
    public void AddPastedImage(byte[] pngBytes)
    {
        if (!CanPasteImages)
        {
            Transcript.Add(new TranscriptEntryViewModel(
                TranscriptEntryKind.Error, "This session's provider does not support image input — the pasted image was not attached."));
            return;
        }

        PendingAttachments.Add(new ImageAttachmentViewModel(pngBytes, a => PendingAttachments.Remove(a)));
    }

    /// <summary>
    /// Appends a finished voice transcript to the input box rather than sending it straight away, so
    /// the operator can proofread the STT/cleanup result before pressing Enter — the SDK session
    /// already has a text input surface, so this reuses it instead of adding a separate send path.
    /// </summary>
    protected override void OnVoiceTextReady(string text) =>
        InputText = string.IsNullOrEmpty(InputText) ? text : $"{InputText} {text}";

    /// <summary>Auto-submit: sends the input box the transcript was just appended to — the same path Enter/Send takes, so a busy session queues it (T8) rather than erroring.</summary>
    protected override void OnVoiceSubmitRequested()
    {
        if (SendCommand.CanExecute(null))
        {
            SendCommand.Execute(null);
        }
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) && PendingAttachments.Count == 0)
        {
            return;
        }

        // Sending before the session has started reaches the CLI process before its I/O is wired and
        // surfaces a raw "Start must be called before I/O" error (#16). Post-#31 a session starts as
        // soon as it is created, so this only bites a failed-to-start panel — guard it with a plain
        // message and keep the typed text rather than clearing it into a raw error. The driver itself is
        // only created once the session starts (#26), so a null session means "not started" too. Queued
        // dispatch never lands here: a queue only exists once a turn was in flight, i.e. after a start.
        if (_runtime is not { IsRunning: true })
        {
            Transcript.Add(new TranscriptEntryViewModel(
                TranscriptEntryKind.Error, "The session has not started yet — nothing was sent."));
            return;
        }

        var text = InputText;
        var images = PendingAttachments
            .Select(a => Core.Sessions.ImageAttachment.FromBytes(a.PngBytes, a.MediaType))
            .ToList();

        InputText = string.Empty;
        PendingAttachments.Clear();

        // The CLI rejects mid-turn input, so while a turn is in flight the message goes onto the local
        // send queue as a cancellable chip and is dispatched when the turn completes (T8), instead of
        // being blocked or silently dropped. The echo row is added at dispatch time so the transcript
        // stays in send order.
        if (IsBusy)
        {
            QueuedMessages.Add(new QueuedMessageViewModel(text, images, m => QueuedMessages.Remove(m)));
            return;
        }

        await _DispatchMessageAsync(text, images);
    }

    /// <summary>
    /// Pulls the most recently queued message back into the input for editing (Arrow Up on an empty
    /// input) — its text and any images are restored and the chip is removed. Returns false when the
    /// queue is empty, so the key handler can let Arrow Up do its normal thing.
    /// </summary>
    public bool RecallLastQueuedMessage()
    {
        if (QueuedMessages.Count == 0)
        {
            return false;
        }

        var last = QueuedMessages[^1];
        QueuedMessages.RemoveAt(QueuedMessages.Count - 1);

        InputText = last.Text;
        foreach (var image in last.Images)
        {
            AddPastedImage(Convert.FromBase64String(image.Base64Data));
        }

        return true;
    }

    /// <summary>Sends a message to the session now, echoing it into the transcript and marking the turn busy.</summary>
    private async Task _DispatchMessageAsync(string text, IReadOnlyList<Core.Sessions.ImageAttachment> images)
    {
        if (_runtime is null)
        {
            return;
        }

        // "exit" closes the session once its turn completes when the operator enabled it (T10). The
        // message is still sent normally so any session-end/Stop-hooks on Claude's side run first; the
        // close then fires from the TurnCompleted handler. Armed at dispatch so a queued "exit" counts too.
        if (AutoCloseOnExit && text.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
        {
            _closeAfterTurn = true;
        }

        var imageSuffix = images.Count == 0
            ? string.Empty
            : $"[+{images.Count} image{(images.Count == 1 ? "" : "s")}]";
        var echo = string.IsNullOrEmpty(text)
            ? imageSuffix
            : images.Count == 0 ? text : $"{text}  {imageSuffix}";
        Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, echo));
        _currentAssistantEntry = null;
        IsBusy = true;
        IsAwaitingResponse = true;
        _needsAttention = false;
        _RecomputeStatus();

        try
        {
            await _runtime.SendUserMessageAsync(text, images);
            await _OfferImagesToSinksAsync(images);
        }
        catch (Exception ex)
        {
            Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, $"Send failed: {ex.Message}"));
            IsBusy = false;
            IsAwaitingResponse = false;
            _RecomputeStatus();
        }
    }

    // Hands the message's images to any plugin that registered a sink (AC-14), so a tracker can attach them to the
    // issue this session tracks. Provider-agnostic and fail-soft (the registry isolates a sink that throws), and a
    // no-op with no images or no registry — this session's message has already been sent either way.
    private async Task _OfferImagesToSinksAsync(IReadOnlyList<Core.Sessions.ImageAttachment> images)
    {
        if (images.Count == 0 || _imageSinkRegistry is not { } sinks)
        {
            return;
        }

        var attachments = images
            .Select((image, index) => new SessionImageAttachment(
                image.MediaType,
                image.Base64Data,
                $"pasted-image-{index + 1}.{_ImageExtension(image.MediaType)}"))
            .ToList();

        await sinks.NotifyAsync(new SessionImageDispatch(PaneId, attachments));
    }

    private static string _ImageExtension(string mediaType) =>
        mediaType.Split('/').LastOrDefault() is { Length: > 0 } subtype ? subtype : "png";

    /// <summary>
    /// Dispatches the next queued message (T8) once a turn frees the session. Fire-and-forget: the
    /// synchronous part of the dispatch flips <see cref="IsBusy"/> back on before the first await, so
    /// the status settles immediately. No-op when the queue is empty.
    /// </summary>
    private void _TryDispatchNextQueued()
    {
        if (QueuedMessages.Count == 0)
        {
            return;
        }

        var next = QueuedMessages[0];
        QueuedMessages.RemoveAt(0);
        _ = _DispatchMessageAsync(next.Text, next.Images);
    }

    [RelayCommand]
    private async Task AllowToolAsync(TranscriptEntryViewModel entry)
    {
        await RespondToPermissionAsync(entry, allow: true);
    }

    [RelayCommand]
    private async Task DenyToolAsync(TranscriptEntryViewModel entry)
    {
        await RespondToPermissionAsync(entry, allow: false);
    }

    /// <summary>Allows the call and persists a rule matching only this exact tool + input for the session's profile.</summary>
    [RelayCommand]
    private async Task AllowAlwaysExactToolAsync(TranscriptEntryViewModel entry)
    {
        await AllowAlwaysAsync(entry, PermissionRuleScope.Exact);
    }

    /// <summary>Allows the call and persists a rule matching every future call to this tool for the session's profile.</summary>
    [RelayCommand]
    private async Task AllowAlwaysWildcardToolAsync(TranscriptEntryViewModel entry)
    {
        await AllowAlwaysAsync(entry, PermissionRuleScope.Wildcard);
    }

    private async Task RespondToPermissionAsync(TranscriptEntryViewModel entry, bool allow)
    {
        if (_runtime is null || entry.ToolUseId is null)
        {
            return;
        }

        entry.PermissionDecision = allow ? "Allowed" : "Denied";
        entry.IsPendingPermission = false;
        await _runtime.RespondToPermissionAsync(entry.ToolUseId, allow);
    }

    private async Task AllowAlwaysAsync(TranscriptEntryViewModel entry, PermissionRuleScope scope)
    {
        if (_runtime is null || entry.ToolUseId is null || entry.ToolName is null)
        {
            return;
        }

        entry.PermissionDecision = scope == PermissionRuleScope.Wildcard
            ? $"Always allowed ({entry.ToolName}:*)"
            : $"Always allowed (exact: {entry.ToolName})";
        entry.IsPendingPermission = false;

        await _runtime.AllowPermissionAlwaysAsync(entry.ToolUseId, entry.ToolName, entry.InputJson ?? "{}", scope);
    }

    /// <summary>Extracts read-aloud sentences from the just-finished turn's assistant prose and enqueues them (#35). No-op when the turn produced no assistant text.</summary>
    private void _EnqueueTurnProseForReadAloud()
    {
        if (_currentTurnAssistantEntries.Count == 0)
        {
            return;
        }

        var markdown = string.Join("\n\n", _currentTurnAssistantEntries.Select(entry => entry.Text));
        _ = EnqueueReadAloudAsync(markdown, TtsVoiceId);
    }

    /// <summary>On-demand read-aloud for a single transcript row (#35) — works regardless of <see cref="ReadResponsesAloud"/>, since the speaker button next to an assistant reply is an explicit request to hear it.</summary>
    [RelayCommand]
    private void ReadAloud(TranscriptEntryViewModel entry)
    {
        if (entry.Kind != TranscriptEntryKind.AssistantText)
        {
            return;
        }

        _ = EnqueueReadAloudAsync(entry.Text, TtsVoiceId);
    }

    // The runtime pumps the driver off the UI thread and raises each event here (#68); marshalling onto the UI
    // thread is this panel's job, because it is the consumer that touches UI — a headless consumer of the same
    // runtime marshals nothing.
    private void _OnSessionEvent(SessionEvent evt) => Dispatcher.UIThread.Post(() => Apply(evt));

    /// <summary>internal (rather than private) so <c>Cockpit.Core.Tests</c> can drive it directly, bypassing <c>Dispatcher.UIThread</c> — see <see cref="_OnSessionEvent"/>.</summary>
    internal void Apply(SessionEvent evt)
    {
        // "Thinking…" tracks the model working with no visible output yet. Any assistant output, a tool
        // call surfacing, a pending permission, or the turn ending clears it; a completed tool result re-arms
        // it, since the model then processes that result before its next output — so activity stays visible
        // across the whole tool round-trip (send → think → tool → run → think → answer).
        if (evt is AssistantThinkingDelta or AssistantTextDelta or AssistantTextCompleted or ToolUseRequested or PermissionRequested or TurnCompleted or SessionError)
        {
            IsAwaitingResponse = false;
        }
        else if (evt is ToolResult)
        {
            IsAwaitingResponse = true;
        }

        switch (evt)
        {
            case SessionInitialized init:
                // The init event is where an SDK session's working directory becomes known — surface it on the
                // shared base so the read/observe surface can report it (a directory-scoped plugin follows this).
                if (!string.IsNullOrEmpty(init.Cwd))
                {
                    WorkingDirectory = init.Cwd;
                }

                Status = string.IsNullOrEmpty(init.Cwd)
                    ? $"Connected ({init.Tools.Count} tools)."
                    : $"Connected ({init.Tools.Count} tools, cwd={init.Cwd}).";
                // The names themselves, on hover, so it is verifiable which tools are available — sorted, because a
                // list you look something up in is sorted, and the order a server happened to announce them in is not
                // an order.
                ConnectedToolsHeading = init.Tools.Count == 0
                    ? "No tools connected — add an MCP server (e.g. filesystem) to give this session tools."
                    : $"{init.Tools.Count} tools connected";

                ConnectedTools.Clear();
                foreach (var tool in init.Tools.OrderBy(tool => tool, StringComparer.OrdinalIgnoreCase))
                {
                    ConnectedTools.Add(tool);
                }

                break;

            case AssistantThinkingDelta thinking:
                if (_currentThinkingEntry is null)
                {
                    _currentThinkingEntry = new TranscriptEntryViewModel(TranscriptEntryKind.Thinking, string.Empty);
                    Transcript.Add(_currentThinkingEntry);
                }

                _currentThinkingEntry.AppendText(thinking.Thinking);
                break;

            case AssistantTextDelta delta:
                // A text delta means the thinking block (if any) for this turn is done.
                _RemoveCurrentThinkingEntry();
                if (_currentAssistantEntry is null)
                {
                    _currentAssistantEntry = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, string.Empty);
                    Transcript.Add(_currentAssistantEntry);
                    _currentTurnAssistantEntries.Add(_currentAssistantEntry);
                }

                _currentAssistantEntry.AppendText(delta.Text);
                break;

            case AssistantTextCompleted completed:
                _RemoveCurrentThinkingEntry();
                if (_currentAssistantEntry is not null)
                {
                    // Streaming deltas already built the text; nothing further to append.
                    _currentAssistantEntry = null;
                }
                else
                {
                    var completedEntry = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, completed.Text);
                    Transcript.Add(completedEntry);
                    _currentTurnAssistantEntries.Add(completedEntry);
                }

                // Assistant prose is one of the two channels a plugin watches for an output signal (the other
                // is tool output below) — e.g. Claude announcing "opened https://github.com/…/pull/5".
                RaiseOutputText(completed.Text);
                break;

            case ToolUseRequested toolUse:
                _RemoveCurrentThinkingEntry();
                Transcript.Add(new TranscriptEntryViewModel(
                    TranscriptEntryKind.ToolUse,
                    $"Tool: {toolUse.ToolName}({toolUse.InputJson})")
                {
                    ToolUseId = toolUse.ToolUseId,
                    ToolName = toolUse.ToolName,
                    InputJson = toolUse.InputJson,
                });
                break;

            case ToolResult toolResult:
                var toolUseEntry = Transcript.LastOrDefault(
                    t => t.Kind == TranscriptEntryKind.ToolUse && t.ToolUseId == toolResult.ToolUseId);
                if (toolUseEntry is not null)
                {
                    // Couple the result to its tool-call row (L14) so it renders as an expandable
                    // section beneath that call, instead of a detached row that loses which call it
                    // belongs to — the pain with parallel tool calls.
                    toolUseEntry.SetResult(toolResult.Content, toolResult.IsError);
                }
                else
                {
                    // No matching tool-use in view (e.g. a result arriving first): fall back to a row.
                    Transcript.Add(new TranscriptEntryViewModel(
                        TranscriptEntryKind.ToolResult,
                        toolResult.IsError ? $"Tool error: {toolResult.Content}" : $"Tool result: {toolResult.Content}"));
                }

                // Tool output is where a shelled-out `gh pr create`/`git push` prints its pull-request url, so
                // it is the primary channel the PR watcher scans (the read/observe surface).
                RaiseOutputText(toolResult.Content);
                break;

            case PermissionRequested permission:
                var entry = Transcript.LastOrDefault(t => t.ToolUseId == permission.ToolUseId);
                if (entry is not null)
                {
                    entry.IsPendingPermission = true;
                }

                _needsAttention = true;
                _RecomputeStatus();
                break;

            case Question question:
                Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Question, question.Text));
                break;

            case TurnCompleted turn:
                _RemoveCurrentThinkingEntry();
                // Only surface a turn row when it failed — a plain "Turn completed (success)" row is
                // noise in the transcript (T4). The Done status still fires below.
                if (turn.IsError)
                {
                    Transcript.Add(new TranscriptEntryViewModel(
                        TranscriptEntryKind.TurnCompleted, $"Turn failed ({turn.Subtype})"));
                }

                if (ReadResponsesAloud)
                {
                    _EnqueueTurnProseForReadAloud();
                }

                _currentTurnAssistantEntries.Clear();
                _currentAssistantEntry = null;
                _hasCompletedATurn = true;
                IsBusy = false;
                _AccumulateUsage(turn);
                _RefreshLimits();
                _RecomputeStatus();
                // "exit" turn finished → ask the cockpit to close this session (T10). Skip draining the
                // queue: the session is going away, so anything still queued is moot.
                if (_closeAfterTurn)
                {
                    _closeAfterTurn = false;
                    RaiseCloseRequested();
                    break;
                }

                // A completed turn (success or error result) frees the session, so send the next queued
                // message (T8). A SessionError event does not drain the queue — the chips stay so a
                // broken session isn't cascaded through every queued message.
                _TryDispatchNextQueued();
                break;

            case SessionError error:
                _RemoveCurrentThinkingEntry();
                Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, error.Message));
                IsBusy = false;
                _RecomputeStatus();
                break;

            case SessionStatusChanged statusChanged:
                // needs_action non-empty is the CLI telling the host the session wants attention
                // (e.g. a pending question) — same "jump out in the sidebar" signal as a pending
                // tool permission. RateLimitInfo/UnknownEvent stay out of scope for status (per-session
                // status overview and agent-tree rendering are later increments); ConsumeEventsAsync
                // already delivers them to any future subscriber.
                if (!string.IsNullOrEmpty(statusChanged.NeedsAction))
                {
                    _needsAttention = true;
                }

                _RecomputeStatus();
                break;

            case RateLimitInfo:
            case UnknownEvent:
                break;
        }
    }

    /// <summary>
    /// Derives <see cref="SessionStatus"/> from the flags this view model already tracks:
    /// busy while a turn is in flight, needs-attention while a permission/needs_action signal is
    /// outstanding (takes priority over busy so it still surfaces if a new send arrives before the
    /// user reacts), done once a turn completed and nothing is pending, idle otherwise.
    /// </summary>
    private void _RecomputeStatus()
    {
        SessionStatus = (_needsAttention, IsBusy) switch
        {
            (true, _) => SessionStatus.NeedsAttention,
            (false, true) => SessionStatus.Busy,
            (false, false) => _hasCompletedATurn ? SessionStatus.Done : SessionStatus.Idle,
        };
    }

    // Fold this turn's reported usage/cost into the running session meter (#8) and refresh the bound
    // meter text. A turn whose result carried no usage (e.g. an error) contributes nothing but is still
    // counted, so the meter simply stays as it was when there is nothing new to add.
    private void _AccumulateUsage(TurnCompleted turn)
    {
        _usage.Add(turn.Usage, turn.TotalCostUsd);
        HasUsage = _usage.HasData;
        UsageSummary = _usage.Summary;
        UsageTooltip = _usage.Tooltip;
    }

    // Pulls the driver's latest limits into the header bars. Read at each turn boundary rather than on a timer:
    // the provider reports how full the context window is when a turn ends, so that is when the numbers change —
    // and a session with no limits feed simply reads null and keeps the bars hidden.
    private void _RefreshLimits()
    {
        if (_runtime?.CurrentStatus is { HasAny: true } status)
        {
            ContextUsedPercent = status.ContextUsedPercent;
            RateLimits.Clear();
            foreach (var window in status.RateLimits)
            {
                RateLimits.Add(window);
            }

            LimitsTooltip = status.Describe();
        }
    }

    /// <summary>
    /// Removes the in-progress "Thinking..." transcript row, if any, once real text/tool-use
    /// output or a turn boundary makes it stale. No-op when there is no current thinking entry.
    /// </summary>
    private void _RemoveCurrentThinkingEntry()
    {
        if (_currentThinkingEntry is null)
        {
            return;
        }

        Transcript.Remove(_currentThinkingEntry);
        _currentThinkingEntry = null;
    }

    protected override async ValueTask DisposeCoreAsync()
    {
        if (_runtime is null)
        {
            return;
        }

        // Stop through the manager, which owns the runtime: the same path an orchestrator's stop_task (#67)
        // takes, so a session ends in one state however it was closed. Unsubscribing first means the teardown
        // cannot post another event at a panel that is going away — and since the pump no longer marshals to
        // the UI thread, killing the child no longer depends on the dispatcher still being alive, which is
        // what used to hang shutdown with a live child claude (#32).
        _runtime.EventAppended -= _OnSessionEvent;
        var runtime = _runtime;
        _runtime = null;

        if (_sessionManager is not null)
        {
            await _sessionManager.StopAsync(runtime.Id);
        }
        else
        {
            await runtime.DisposeAsync();
        }
    }
}
