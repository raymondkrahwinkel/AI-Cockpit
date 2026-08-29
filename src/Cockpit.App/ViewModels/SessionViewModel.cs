using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Mentions;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Usage;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.ViewModels;

// F-C1 cockpit: a single Claude Code session rendered as a streaming transcript with a chat-style input box and
// read-only-so-far allow/deny affordances for tool use.
public partial class SessionViewModel : SessionPanelViewModel, ITransientService
{
    private readonly ISessionManager? _sessionManager;

    // AC-409: written on a live permission-mode switch (see `OnSelectedPermissionModeChanged`). Null in the design-time/unit-test graph, where the switch simply is not persisted.
    private readonly SessionStateRecorder? _sessionStateRecorder;

    // Resolves a Plugin-provider profile's own display name for the header's kind chip (AC-537) — the same registry
    // `Converters.ProfileDisplayConverter` uses for the profile picker, injected here rather than reaching into that
    // converter's static seam.
    private readonly IPluginProviderRegistry? _pluginProviderRegistry;

    // AC-713: the generic login gate/starter, dispatched to whichever provider plugin the profile below names.
    private readonly IProfileLoginChecker? _loginChecker;
    private readonly IProfileLoginStarter? _loginStarter;

    // AC-740: null in the design-time/unit-test graph, where the @-mention picker's file source always answers empty.
    private readonly IMentionFileSource? _mentionFileSource;

    // AC-775: the process-wide usage cache shared across sessions on the same underlying credential. Null in
    // the design-time/unit-test graph, where `_RefreshLimits` simply falls back to the driver's own status.
    private readonly ISharedUsageCache? _sharedUsageCache;

    // AC-1239: a swallowed launch failure left no trace anywhere. Null in the design-time/unit-test graph.
    private readonly ILogger<SessionViewModel>? _logger;

    // AC-713: the profile this session started under — what an auth error or the poll timer below check against.
    private SessionProfile? _profile;

    // AC-713: polls `_loginChecker.IsLoggedIn(_profile)` for the auth-expiry bar, since the SDK route has no TTY pane to show a login prompt in on its own.
    private DispatcherTimer? _loginPollTimer;

    // AC-761 F3: catches a `get_usage`/`get_context_usage` reply that missed its turn's publish grace — a plain
    // re-read of `_runtime.CurrentStatus`, no CLI traffic of its own, for a session that has since gone idle.
    private DispatcherTimer? _usageCatchUpTimer;

    // The session itself — driver, event pump, lifetime — lives in the runtime (#68); this panel is one of its
    // consumers, not its owner. Created once the profile (and therefore the provider) is known, in
    // StartWithProfileAsync. The manager owns it and is the one place it gets stopped.
    private ISessionRuntime? _runtime;

    // The offer this pane was restored with, captured at the top of `StartConfiguredAsync` when it is still set
    // (AC-410).
    private SessionRestorePlan? _restoredOfferSnapshot;

    // The per-session plugin-provider launch options (sandbox, model) from the New-session dialog, set the same way as `SessionPanelViewModel.McpServerSelection` just before `StartWithProfileAsync` reads them.
    private IReadOnlyDictionary<string, string>? _launchOptions;

    // Tool names this session auto-allows without an operator prompt (AC-215).
    private IReadOnlySet<string> _preApprovedTools = new HashSet<string>(StringComparer.Ordinal);

    // Whether this session auto-allows every tool call without a prompt (AC-215, Raymond 2026-07-23) — the "worktree is
    // the boundary" stance for an autonomous run isolated in a throwaway worktree, which must run its work tools (Bash,
    // edits, git) with no one to answer a prompt.
    private bool _preApproveAllTools;

    private TranscriptEntryViewModel? _currentAssistantEntry;

    // The reasoning/thinking row currently being streamed into (AC-213), or null when no thinking block is open. Mirrors `_currentAssistantEntry`: contiguous thinking deltas append onto one row rather than spawning a row per delta.
    private TranscriptEntryViewModel? _currentThinkingEntry;

    // The provider block index of `_currentThinkingEntry`; a delta from a different block (e.g. Codex's raw reasoning vs. its summary) starts a fresh row so the two never concatenate.
    private int _currentThinkingBlockIndex = -1;

    // Assistant-text rows added since the last `TurnCompleted` — a turn can produce several (text, tool call, more text), so the read-aloud trigger (#35) reads all of them, not just the last.
    private readonly List<TranscriptEntryViewModel> _currentTurnAssistantEntries = [];

    // One sub-agent's own streaming state (AC-146).
    private sealed class SubAgentLane(TranscriptEntryViewModel anchor)
    {
        public TranscriptEntryViewModel Anchor { get; } = anchor;
        public TranscriptEntryViewModel? CurrentAssistantEntry { get; set; }
        public TranscriptEntryViewModel? CurrentThinkingEntry { get; set; }
        public int CurrentThinkingBlockIndex { get; set; } = -1;
    }

    // Live sub-agent lanes, keyed by the parent Task tool call's own tool_use_id. Cleared on every `TurnCompleted`: a sub-agent does not outlive the turn that spawned it.
    private readonly Dictionary<string, SubAgentLane> _subAgentLanes = [];

    // One top-level tool call the turn is currently waiting on (AC-532).
    private readonly record struct ActiveToolCall(string ToolUseId, string Label, DateTimeOffset StartedAt);

    // Provider-neutral by construction: driven only by `ToolUseRequested`/`ToolResult`, the two events every provider
    // that reports tool calls at all raises (AC-532).
    private readonly List<ActiveToolCall> _activeToolCalls = [];

    // True while a top-level tool call is outstanding — drives the composer's activity band in place of "Thinking…" (AC-532).
    public bool HasActiveToolActivity => _activeToolCalls.Count > 0;

    // The call the composer's activity band currently reflects (AC-532): the oldest outstanding call still waiting on a
    // permission decision, if any.
    private ActiveToolCall? _CurrentActiveToolCall()
    {
        if (_activeToolCalls.Count == 0)
        {
            return null;
        }

        foreach (var call in _activeToolCalls)
        {
            if (_IsAwaitingPermission(call.ToolUseId))
            {
                return call;
            }
        }

        return _activeToolCalls[^1];
    }

    // Whether the transcript row for this outstanding tool call is currently paused on a permission prompt
    // (AC-532) — read straight from `TranscriptEntryViewModel.IsPendingPermission`, the same flag the
    // pending-permission chip already renders from, rather than a second ledger tracking the same fact.
    private bool _IsAwaitingPermission(string toolUseId) =>
        Transcript.LastOrDefault(t => t.ToolUseId == toolUseId)?.IsPendingPermission ?? false;

    // The currently-shown activity's label ("Bash  ·  dotnet build"), or empty when none is active.
    public string ActiveToolActivityLabel => _CurrentActiveToolCall()?.Label ?? string.Empty;

    // While that call is paused on a permission prompt this reads "waiting for permission" instead: the tool is not
    // running, it is blocked on the operator, and a still-climbing number under a "running" label would misreport a
    // human wait as tool work.
    public string ActiveToolActivityAgeText
    {
        get
        {
            if (_CurrentActiveToolCall() is not { } call)
            {
                return string.Empty;
            }

            if (!_IsAwaitingPermission(call.ToolUseId))
            {
                return $"running {_FormatElapsed(DateTimeOffset.Now - call.StartedAt)}";
            }

            // AC-715: a clarifying question is blocked on an answer, not on consent — "waiting for permission"
            // sends the operator looking for an Allow button that is deliberately not there.
            return Transcript.LastOrDefault(row => row.ToolUseId == call.ToolUseId)?.HasQuestionPrompts == true
                ? "waiting for an answer"
                : "waiting for permission";
        }
    }

    // Re-raises the age text's change notification (AC-532) — called on a view-owned tick so the composer's elapsed time counts up instead of freezing at whatever it read on first render.
    public void RefreshActiveToolActivityAge() => OnPropertyChanged(nameof(ActiveToolActivityAgeText));

    // "m:ss", matching the approved mockup's notation (e.g. "0:12", "1:05") — the composer band is the first place this ships, so this is the notation a later background-task pop-out (AC-531) follows rather than inventing its own.
    internal static string _FormatElapsed(TimeSpan elapsed)
    {
        var totalSeconds = Math.Max(0, (int)elapsed.TotalSeconds);
        return $"{totalSeconds / 60}:{totalSeconds % 60:00}";
    }

    // The two bands occupy the same composer slot and are never both visible — the activity band replaces "Thinking…"
    // for the span it is active, rather than stacking on top (composer height must not grow) (AC-532).
    public bool ShowThinkingIndicator => IsBusy && !HasActiveToolActivity;

    // Raises every notification the active-tool-activity fields need after `_activeToolCalls` changes.
    private void _RaiseActiveToolActivityChanged()
    {
        OnPropertyChanged(nameof(HasActiveToolActivity));
        OnPropertyChanged(nameof(ActiveToolActivityLabel));
        OnPropertyChanged(nameof(ActiveToolActivityAgeText));
        OnPropertyChanged(nameof(ShowThinkingIndicator));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ShowThinkingIndicator));

    // A TaskId no longer in the latest snapshot is removed rather than kept: if the same id is ever reused, it starts a
    // fresh clock instead of resuming a stale one (AC-531).
    private readonly Dictionary<string, DateTimeOffset> _backgroundTaskFirstSeen = [];

    // Outstanding sub-agents, shells and unrecognised-kind tasks (AC-531), grouped the way the approved mockup
    // groups them. Built from the same `_backgroundTasks` list `HasOutstandingBackgroundShells`
    // already reads — the pop-out's own view of the identical, provider-neutral ledger, not a second one.
    public ObservableCollection<BackgroundTaskViewModel> BackgroundSubAgents { get; } = [];

    public ObservableCollection<BackgroundTaskViewModel> BackgroundShells { get; } = [];

    // A task kind this build does not recognise — carried rather than dropped, same reasoning as the
    // provider's own wire parser (see `BackgroundTaskKind.Unknown`).
    public ObservableCollection<BackgroundTaskViewModel> BackgroundOtherTasks { get; } = [];

    public bool HasBackgroundSubAgents => BackgroundSubAgents.Count > 0;

    public bool HasBackgroundShells => BackgroundShells.Count > 0;

    public bool HasBackgroundOtherTasks => BackgroundOtherTasks.Count > 0;

    // True while at least one background task is outstanding. This gates the pop-out's own contents (list vs.
    // "no background work"); the button itself is always shown, and only its count badge follows this too
    // (AC-531 #2 — no badge at all at zero, not a "0" badge).
    public bool HasBackgroundTasks => _backgroundTasks.Count > 0;

    // The button's badge digit — every outstanding task counts, including a kind this build does not
    // recognise (AC-531 #2).
    public int BackgroundTaskCount => _backgroundTasks.Count;

    // "2 sub-agents · 1 shell" — the pop-out's own total line, segments joined the same way AC-532's activity
    // band joins its own. "nothing" when the list is empty (AC-531 #3, the mockup's empty state).
    public string BackgroundTaskSummary
    {
        get
        {
            if (_backgroundTasks.Count == 0)
            {
                return "nothing";
            }

            var parts = new List<string>();
            if (BackgroundSubAgents.Count > 0)
            {
                parts.Add(BackgroundSubAgents.Count == 1 ? "1 sub-agent" : $"{BackgroundSubAgents.Count} sub-agents");
            }

            if (BackgroundShells.Count > 0)
            {
                parts.Add(BackgroundShells.Count == 1 ? "1 shell" : $"{BackgroundShells.Count} shells");
            }

            if (BackgroundOtherTasks.Count > 0)
            {
                parts.Add(BackgroundOtherTasks.Count == 1 ? "1 other" : $"{BackgroundOtherTasks.Count} other");
            }

            return string.Join(" · ", parts);
        }
    }

    // Selects (or, on a second click of the same row, collapses) one background task's detail in the
    // pop-out (AC-531 #4). Only one row expands at a time, mirroring the mockup.
    public void ToggleBackgroundTaskSelection(BackgroundTaskViewModel task)
    {
        var makeSelected = !task.IsSelected;
        foreach (var row in BackgroundSubAgents.Concat(BackgroundShells).Concat(BackgroundOtherTasks))
        {
            row.IsSelected = false;
        }

        task.IsSelected = makeSelected;
    }

    // Reuses row instances by TaskId rather than recreating them, so a row the operator has expanded stays expanded
    // across an unrelated task starting or ending elsewhere in the list (AC-531).
    private void _RebuildBackgroundTaskRows()
    {
        var now = DateTimeOffset.Now;
        var liveIds = new HashSet<string>();
        foreach (var task in _backgroundTasks)
        {
            liveIds.Add(task.TaskId);
            if (!_backgroundTaskFirstSeen.ContainsKey(task.TaskId))
            {
                _backgroundTaskFirstSeen[task.TaskId] = now;
            }
        }

        // A TaskId no longer reported has finished (or the whole set was wiped, e.g. SessionError): forget its
        // clock so a reused id someday starts fresh rather than resuming a stale one (AC-531 #8).
        foreach (var staleId in _backgroundTaskFirstSeen.Keys.Where(id => !liveIds.Contains(id)).ToList())
        {
            _backgroundTaskFirstSeen.Remove(staleId);
        }

        _SyncBackgroundGroup(BackgroundSubAgents, _backgroundTasks.Where(task => task.Kind == BackgroundTaskKind.SubAgent));
        _SyncBackgroundGroup(BackgroundShells, _backgroundTasks.Where(task => task.Kind == BackgroundTaskKind.Shell));
        _SyncBackgroundGroup(BackgroundOtherTasks, _backgroundTasks.Where(task => task.Kind == BackgroundTaskKind.Unknown));

        OnPropertyChanged(nameof(HasBackgroundSubAgents));
        OnPropertyChanged(nameof(HasBackgroundShells));
        OnPropertyChanged(nameof(HasBackgroundOtherTasks));
        OnPropertyChanged(nameof(HasBackgroundTasks));
        OnPropertyChanged(nameof(BackgroundTaskCount));
        OnPropertyChanged(nameof(BackgroundTaskSummary));

        foreach (var row in _backgroundToolRows)
        {
            row.IsBackgroundTaskLive = liveIds.Contains(row.BackgroundTaskId!);
        }
    }

    // The transcript rows whose tool call named a background task (AC-1056), so each one's badge can follow the
    // same ledger the pop-out above does. Only ever holds rows that carry an id, which is why the read is a `!`.
    private readonly List<TranscriptEntryViewModel> _backgroundToolRows = [];

    // Starts following a row whose just-arrived result announced a background task, seeding it from the current
    // ledger: the task is normally already reported by the time its own result lands.
    private void _TrackBackgroundToolRow(TranscriptEntryViewModel row)
    {
        if (row.BackgroundTaskId is null)
        {
            return;
        }

        _backgroundToolRows.Add(row);
        row.IsBackgroundTaskLive = _backgroundTasks.Any(task => task.TaskId == row.BackgroundTaskId);
    }

    // Adds/removes/updates rows in one kind's group to match `tasks`, keeping the
    // existing `BackgroundTaskViewModel` instance for a TaskId that is still present.
    private void _SyncBackgroundGroup(ObservableCollection<BackgroundTaskViewModel> group, IEnumerable<BackgroundTask> tasks)
    {
        var incoming = tasks.ToList();
        var incomingIds = incoming.Select(task => task.TaskId).ToHashSet();

        for (var i = group.Count - 1; i >= 0; i--)
        {
            if (!incomingIds.Contains(group[i].TaskId))
            {
                group.RemoveAt(i);
            }
        }

        foreach (var task in incoming)
        {
            var existing = group.FirstOrDefault(row => row.TaskId == task.TaskId);
            if (existing is null)
            {
                group.Add(new BackgroundTaskViewModel(
                    task.TaskId, task.Kind, task.Description, _backgroundTaskFirstSeen[task.TaskId], ToggleBackgroundTaskSelection));
            }
            else
            {
                existing.UpdateDescription(task.Description);
            }
        }
    }

    // Re-raises AgeText's change notification for every row currently listed (AC-531 #8) — called on
    // the same view-owned tick `RefreshActiveToolActivityAge` uses, so the pop-out's elapsed times
    // count up instead of freezing at whatever they read on first render. A no-op with nothing outstanding.
    public void RefreshBackgroundTaskAges()
    {
        foreach (var row in BackgroundSubAgents.Concat(BackgroundShells).Concat(BackgroundOtherTasks))
        {
            row.RaiseAgeChanged();
        }
    }

    // Keep orphaned sub-agent text separate so it cannot merge into the top-level reply or be read aloud (AC-146).
    private TranscriptEntryViewModel? _currentOrphanedSubAgentTextEntry;

    // The transcript row for a tool call — the same shape wherever one is built, top-level or in a sub-agent lane.
    private static TranscriptEntryViewModel _ToolUseRow(string toolUseId, string toolName, string inputJson) =>
        new(TranscriptEntryKind.ToolUse, $"Tool: {toolName}({inputJson})")
        {
            ToolUseId = toolUseId,
            ToolName = toolName,
            InputJson = inputJson,
        };

    // AC-996: the row a permission asks about, when no tool-use event ever brought one. Top-level even for a
    // sub-agent's call: a row nested under a collapsed anchor is exactly the kind the operator cannot reach, and
    // being asked is the whole reason this row exists.
    private TranscriptEntryViewModel _AddOrphanPermissionRow(PermissionRequested permission)
    {
        var row = _ToolUseRow(permission.ToolUseId, permission.ToolName, permission.InputJson);
        Transcript.Add(row);
        return row;
    }

    // Null for a top-level event (no parent id) or one naming a parent this pane never saw the tool-use row for
    // (AC-146).
    private SubAgentLane? _ResolveSubAgentLane(string? parentToolUseId)
    {
        if (string.IsNullOrEmpty(parentToolUseId))
        {
            return null;
        }

        if (_subAgentLanes.TryGetValue(parentToolUseId, out var lane))
        {
            return lane;
        }

        var anchor = Transcript.LastOrDefault(t => t.Kind == TranscriptEntryKind.ToolUse && t.ToolUseId == parentToolUseId);
        if (anchor is null)
        {
            return null;
        }

        lane = new SubAgentLane(anchor);
        _subAgentLanes[parentToolUseId] = lane;
        return lane;
    }

    // A turn pauses on a question/permission and then keeps streaming into the same growing entry afterwards (AC-97).
    private int _readAloudFlushedLength;

    // AC-597/598: whether anything has actually been spoken this turn. What decides both fillers — a lead-in is
    // only owed when the model gave none, and a sign of life only when the operator has heard nothing since.
    private bool _spokenSomethingThisTurn;

    // Rotated so the same words never come twice in a row. Deliberately not reset per turn: it is the sequence of
    // fillers the operator hears that has to vary, not the sequence within one turn.
    private int _spokenFillerRotation;

    // AC-598: the clock that says "still on it" through a long wait, and how many times it has said so this turn.
    private DispatcherTimer? _signOfLifeTimer;

    private int _signOfLifeRepeat;

    // Set when an "exit" message is dispatched with auto-close on, so the next completed turn closes the session (T10).
    private bool _closeAfterTurn;

    // The most recently dispatched user turn (text + images), so a failed TurnCompleted row's Retry action
    // (AC-728) can resend exactly what was sent — the operator does not have to retype it.
    private (string Text, IReadOnlyList<Core.Sessions.ImageAttachment> Images)? _lastDispatchedUserTurn;

    // AC-1031: set right after StopAsync's own InterruptAsync call succeeds, consumed by the next TurnCompleted —
    // the CLI reports an interrupted turn the same way as a real driver failure, and this is the only place that
    // knows the operator asked for the stop.
    private bool _interruptRequested;

    public ObservableCollection<TranscriptEntryViewModel> Transcript { get; } = [];

    // AC-800: the rows the reading level shows, in transcript order — what the transcript views bind to. A hidden
    // row used to stay an item at zero height, so the panel built ten TranscriptRowViews per visible row.
    // `Transcript` stays the structure of record: `_FormGroups` walks it by index.
    public ObservableCollection<TranscriptEntryViewModel> VisibleTranscript { get; } = [];

    // Which rows are currently in `VisibleTranscript`, so a re-announcement that changes nothing costs nothing:
    // `_FormGroups` re-stamps every member of a run on each append, and each stamp raises `IsRowVisible` whether or
    // not the answer moved. Acting on the announcement rather than on the change would make an append O(run).
    private readonly HashSet<TranscriptEntryViewModel> _shown = [];

    // Set while a bulk change (a reading-level switch, a whole-transcript regroup) is in flight: every row is about
    // to be re-announced, so one rebuild afterwards beats n insert/remove pairs, each of which scans.
    private bool _suspendVisibleSync;

    // False until the first transcript row arrives, so the panel can show a calm empty-state hint instead of a void.
    public bool HasTranscript => Transcript.Count > 0;

    // Gates the empty-state's "type to start" prompt so it only invites input once the session is actually ready.
    public virtual bool IsSessionReady => _runtime is { IsRunning: true };

    // The headless route is the one with no `/clear` of its own, so this is where the action belongs (AC-564).
    public override bool SupportsClearContext => true;

    // True from launch until the runtime settles — up *or* failed. Drives the "still starting"
    // banner so it shows only while the session is actively coming up, and never sits stuck reading "starting"
    // after a launch that failed (where the runtime is assigned but never running).
    [ObservableProperty]
    private bool _isStarting;

    // AC-1239: why the last launch did not take, else null. A start that failed and one still coming up both leave
    // `IsSessionReady` false, so this is the only thing that tells them apart — and it carries the reason.
    [ObservableProperty]
    private string? _startFailure;

    // Images pasted into the input, sent with the next message and cleared afterwards.
    public ObservableCollection<ImageAttachmentViewModel> PendingAttachments { get; } = [];

    // True while at least one image is queued, so the chip strip can hide when empty.
    public bool HasPendingAttachments => PendingAttachments.Count > 0;

    // True when this session's driver actually sends pasted images to the model (#64) — gates `AddPastedImage` so a
    // provider without `SessionCapabilities.SupportsVision` (Ollama/LM Studio, the current plugin providers) never
    // silently drops a pasted image.
    public bool CanPasteImages => Capabilities is { SupportsVision: true };

    // Messages typed while a turn was in flight, dispatched in order as turns complete (T8).
    public ObservableCollection<QueuedMessageViewModel> QueuedMessages { get; } = [];

    // True while the send queue holds a message, so the queued-chip strip can hide when empty.
    public bool HasQueuedMessages => QueuedMessages.Count > 0;

    // The message a row's reply button set as this composer's target (AC-935), shown as a dismissible citation
    // chip above the input and consumed at the same point PendingAttachments is — set by SetReplyTarget, cleared
    // by ClearReplyTarget or by a send going through.
    [ObservableProperty]
    private TranscriptEntryViewModel? _pendingReplyTo;

    public bool HasPendingReplyTo => PendingReplyTo is not null;

    // The composer chip's own citation of the pending target — same helper the sent reply row uses, so the
    // operator sees before sending exactly what the model will be told afterwards.
    public string PendingReplyExcerpt => PendingReplyTo is null
        ? string.Empty
        : TranscriptEntryViewModel.BuildReplyExcerpt(PendingReplyTo.TextWithImageSuffix);

    partial void OnPendingReplyToChanged(TranscriptEntryViewModel? value)
    {
        OnPropertyChanged(nameof(HasPendingReplyTo));
        OnPropertyChanged(nameof(PendingReplyExcerpt));
    }

    // A row's reply button (AC-935) — sets the composer's target; consumed and cleared at dispatch.
    [RelayCommand]
    private void SetReplyTarget(TranscriptEntryViewModel target) => PendingReplyTo = target;

    // The chip's own cancel (AC-935).
    [RelayCommand]
    private void ClearReplyTarget() => PendingReplyTo = null;

    // AC-740: the @-mention file-/folder-picker. Reads WorkingDirectory lazily on every '@' rather than once at
    // construction — this session's own working directory is unset until launch, same reasoning as the
    // Assistant-chat host that shares this view model.
    public MentionPickerViewModel MentionPicker { get; }

    // When on, every message queued while a turn was in flight is dispatched together as a single follow-up turn once
    // the turn completes (AC-145), instead of one-per-turn.
    [ObservableProperty]
    private bool _combineQueuedMessages;

    // True when there is text or an image to act on, so Send is enabled exactly when it will do
    // something. It does not gate on `IsBusy`: while a turn runs, Send queues the message
    // (T8) rather than being disabled, so you can keep typing ahead without losing input.
    public bool CanSend => !string.IsNullOrWhiteSpace(InputText) || PendingAttachments.Count > 0;

    // It gates only the *view* (the input box is disabled), deliberately not `CanSend`: the host still submits the
    // run's opening brief through the send path programmatically, which must work even while the composer is off
    // (AC-174).
    [ObservableProperty]
    private bool _isInputEnabled = true;

    // Permission modes offered in the running panel: the three live-switchable modes
    // (`SessionOptionCatalog.LivePermissionModes`), or — once a session was launched in bypass — a single locked
    // "Bypass permissions" entry, since the CLI cannot switch a running session into or out of bypass.
    public IReadOnlyList<PermissionModeOption> PermissionModes =>
        IsPermissionModeLocked ? [SelectedPermissionMode] : SessionOptionCatalog.LivePermissionModes;

    [ObservableProperty]
    private PermissionModeOption _selectedPermissionMode = SessionOptionCatalog.DefaultPermissionMode;

    // True once the session was launched in bypass: bypass is terminal (launch-only), so the panel
    // dropdown is disabled rather than offering a switch the CLI would reject — no dead control (#15).
    [ObservableProperty]
    private bool _isPermissionModeLocked;

    partial void OnIsPermissionModeLockedChanged(bool value) => OnPropertyChanged(nameof(PermissionModes));

    // The Claude model aliases suggested in the editable model field; the field stays free text so a specific model or snapshot can be pinned live, matching the New-session dialog.
    public IReadOnlyList<string> ClaudeModelSuggestions => SessionOptionCatalog.ClaudeModelSuggestions;

    // The running session's model of record: the launch `--model`, and what a live switch updates. The header
    // edits it through `LiveModelText` rather than binding here directly, so a switch applies on commit
    // (Enter/focus-loss) instead of on every keystroke.
    [ObservableProperty]
    private ModelOption _selectedModel = SessionOptionCatalog.DefaultModel;

    // The editable text in the header's Claude model field. Setting it has no side effect — the live switch fires
    // only when `CommitLiveModel` is called (the view commits on Enter, focus-loss, or picking a
    // suggestion), so typing a snapshot name does not fire a set_model control request per character.
    [ObservableProperty]
    private string _liveModelText = SessionOptionCatalog.DefaultModel.Value;

    // Thinking-effort levels offered per session; drives the thinking-budget control.
    public IReadOnlyList<EffortOption> Efforts => SessionOptionCatalog.Efforts;

    [ObservableProperty]
    private EffortOption _selectedEffort = SessionOptionCatalog.DefaultEffort;

    // The running plugin provider's generic live controls (#45 D4) — Codex's model and effort — populated after
    // start from the driver's declared options. Empty for Claude and local sessions, which drive their controls
    // through the typed dropdowns above; a provider with nothing to switch leaves the panel hidden.
    public ObservableCollection<LiveControlViewModel> LiveControls { get; } = [];

    // True once the running provider declared at least one generic live control, so the panel shows only when it has something in it.
    public bool HasLiveControls => LiveControls.Count > 0;

    [ObservableProperty]
    private string _inputText = string.Empty;

    // Status now lives on the shared SessionPanelViewModel base (AC-37), read by the one SessionHeaderBar.

    // How many tools this session connected, or why there are none — the line the empty-state card introduces a fresh
    // session with (AC-563, AC-537).
    [ObservableProperty]
    private string _connectedToolsHeading = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompactCommand))]
    private bool _isBusy;


    // Shows the "Allow all tools" toggle: a local tool session (has tools, but not Claude's own permission modes) whose every MCP call would otherwise need an Allow click.
    [ObservableProperty]
    private bool _showToolAutoApprove;

    // When on, this session runs tool calls without prompting (still shown as tool rows). Applied live to the driver.
    [ObservableProperty]
    private bool _autoApproveTools;

    // True while a pending permission decision or CLI `needs_action` signal is outstanding, driving `SessionStatus.NeedsAttention`.
    private bool _needsAttention;

    // True once at least one turn has finished, so an idle session reads as Done rather than Idle — independent of whether a (success) turn added a transcript row (T4).
    private bool _hasCompletedATurn;

    // Carries messages other agents left for this pane out with its next turn (AC-394). Optional: a pane built
    // without it — every design-time and most test constructions — simply sends what it was given, which is the
    // behaviour every session had before this existed.
    private readonly IAgentTurnInboxDelivery? _turnInboxDelivery;

    // Whether the operator drives this session or a plugin embedded it (AC-251). Set by the host when it embeds.
    internal UsageRunKind RunKind { get; set; } = UsageRunKind.Interactive;

    // The run this session was embedded for, from `EmbeddedSessionRequest.RunId`; null for a session belonging to no run.
    internal string? RunId { get; set; }

    // The run's human name, from `EmbeddedSessionRequest.RunLabel`.
    internal string? RunLabel { get; set; }

    // HasUsage, UsageSummary and UsageTooltip now live on the shared SessionPanelViewModel base (AC-37), rendered by
    // the one SessionHeaderBar; _usage still folds each turn's usage into them here.

    // ContextUsedPercent, RateLimits and LimitsTooltip now live on the shared SessionPanelViewModel base (AC-37),
    // so the one SessionHeaderBar control reads the same usage data for every session kind.

    // --- Reading level (AC-138) -------------------------------------------------------------------------------

    // The three reading levels offered by this SDK session's header "View" dropdown.
    public IReadOnlyList<ReadingLevelOption> ReadingLevels => SessionOptionCatalog.ReadingLevels;

    // This SDK session's current reading level (AC-138) — Developer/Focus/Simple. Seeded at start from the
    // per-session override or the profile's default view, and switchable live from the header "View" dropdown.
    // Only the SDK session carries one; a TTY session is a raw terminal with no reading level.
    [ObservableProperty]
    private ReadingLevel _readingLevel = ReadingLevel.Developer;

    // Only Simple hides the standalone "$" token/cost meter unconditionally (AC-138: "no cost" is that level's
    // plain-language promise) (AC-105, AC-536).
    protected override bool SuppressCostMeter => ReadingLevel == ReadingLevel.Simple;

    // Simple drops the model/provider kind chip (AC-138) — a tag that is jargon the level exists to hide.
    public override bool ShowKindChip => ReadingLevel != ReadingLevel.Simple && !string.IsNullOrEmpty(KindLabel);

    partial void OnReadingLevelChanged(ReadingLevel value)
    {
        // The level lives on the session, but each transcript row renders itself from its own copy — push the new
        // level down, re-fold the Focus groups for it, and re-announce the header figures the level shows or hides.
        _suspendVisibleSync = true;
        try
        {
            foreach (var entry in Transcript)
            {
                entry.ReadingLevel = value;
            }

            _RecomputeReadingGroups();
        }
        finally
        {
            _suspendVisibleSync = false;
        }

        _RebuildVisibleTranscript();
        // Rebuild rather than just re-announce: the token/cost figure is a pill segment now, and Simple drops it
        // (SuppressCostMeter), so switching level changes which segments exist rather than one visibility flag.
        RebuildUsagePillItems();
        OnPropertyChanged(nameof(ShowKindChip));
    }

    // Assigns the session's reading level to each row as it arrives, watches a tool row for a permission decision
    // (which changes whether it folds), and re-forms the fold groups — the one place the transcript's structure is
    // read to group rows, since a single row cannot see its neighbours.
    private void _OnTranscriptChanged(NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (TranscriptEntryViewModel entry in e.NewItems)
            {
                entry.Session = this;
                entry.ReadingLevel = ReadingLevel;
                entry.PropertyChanged += _OnEntryPropertyChanged;
            }

            // An append only ever changes the run it lands in, so re-fold that run instead of the whole
            // transcript — the full walk was O(n) per row, O(n²) over a session's life, on the UI thread (AC-787).
            if (e.Action is NotifyCollectionChangedAction.Add && e.NewStartingIndex >= 0)
            {
                _RegroupAround(e.NewStartingIndex, e.NewStartingIndex + e.NewItems.Count - 1);
                foreach (TranscriptEntryViewModel entry in e.NewItems)
                {
                    _SyncVisibleRow(entry);
                }

                return;
            }
        }

        // A removal, a replace or a Clear: the rows that moved are not the ones announced, so rebuild rather than
        // reconcile. Every such change is either a reset or an operator action, never the streaming path.
        _suspendVisibleSync = true;
        try
        {
            _RecomputeReadingGroups();
        }
        finally
        {
            _suspendVisibleSync = false;
        }

        _RebuildVisibleTranscript();
    }

    // Puts one row in or out of `VisibleTranscript` when — and only when — its visibility actually moved.
    private void _SyncVisibleRow(TranscriptEntryViewModel entry)
    {
        if (_suspendVisibleSync || entry.IsRowVisible == _shown.Contains(entry))
        {
            return;
        }

        if (entry.IsRowVisible)
        {
            _shown.Add(entry);
            VisibleTranscript.Insert(_VisibleInsertionPoint(entry), entry);
        }
        else
        {
            _shown.Remove(entry);
            VisibleTranscript.Remove(entry);
        }
    }

    // Where a newly shown row belongs: straight after the nearest shown row above it in the transcript. Walking up
    // from the row rather than counting down from zero is what keeps the streaming case cheap — an appended row has
    // at most its own folded run above it before it meets one that is shown.
    private int _VisibleInsertionPoint(TranscriptEntryViewModel entry)
    {
        for (var index = _IndexOfLast(entry) - 1; index >= 0; index--)
        {
            var above = Transcript[index];
            if (!_shown.Contains(above))
            {
                continue;
            }

            // The append path lands here: the row above is the last one shown, so there is nothing to scan for.
            // ponytail: the fallback is O(shown) per newly shown row — fine for expanding a run, revisit with an
            // index map if a fold toggle on a very long transcript ever feels slow.
            return VisibleTranscript.Count > 0 && ReferenceEquals(VisibleTranscript[^1], above)
                ? VisibleTranscript.Count
                : VisibleTranscript.IndexOf(above) + 1;
        }

        return 0;
    }

    // One pass over the transcript, for the changes that touch every row at once.
    private void _RebuildVisibleTranscript()
    {
        _shown.Clear();
        VisibleTranscript.Clear();
        foreach (var entry in Transcript)
        {
            if (entry.IsRowVisible)
            {
                _shown.Add(entry);
                VisibleTranscript.Add(entry);
            }
        }
    }

    private void _OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Every path that can change what a row shows — a level switch, a fold toggle, a consent landing — ends in
        // `_RaiseReadingLevelPresentation`, so this one announcement is the whole signal `VisibleTranscript` needs.
        if (e.PropertyName is nameof(TranscriptEntryViewModel.IsRowVisible) && sender is TranscriptEntryViewModel row)
        {
            _SyncVisibleRow(row);
        }

        // A tool row is added as auto and can turn into a consent row a beat later (the permission request lands after
        // the tool-use event), which pulls it out of any auto-fold run — so re-fold when either flag flips.
        if (e.PropertyName is nameof(TranscriptEntryViewModel.IsPendingPermission) or nameof(TranscriptEntryViewModel.PermissionDecision))
        {
            if (sender is TranscriptEntryViewModel entry && _IndexOfLast(entry) is var index and >= 0)
            {
                _RegroupAround(index, index);
            }

            OnPropertyChanged(nameof(HasPendingPermission));
        }
    }

    // Searched from the end: the row a permission lands on is the turn's newest, so this stops within a few rows
    // rather than walking a transcript that grows all session. -1 for a row already dropped from the transcript.
    private int _IndexOfLast(TranscriptEntryViewModel entry)
    {
        for (var index = Transcript.Count - 1; index >= 0; index--)
        {
            if (ReferenceEquals(Transcript[index], entry))
            {
                return index;
            }
        }

        return -1;
    }

    // Distinct from `SessionStatus.NeedsAttention`, which is deliberately stickier: `_needsAttention` is set when a
    // prompt appears and cleared only when the operator sends the next message, so a session keeps flagging itself in
    // the sidebar until someone has actually been back to it.
    public bool HasPendingPermission => Transcript.Any(entry => entry.IsPendingPermission);

    // Only Focus folds — Developer shows every row, Simple hides auto tools outright — so at the other levels every row
    // is simply un-grouped (AC-138).
    private void _RecomputeReadingGroups()
    {
        if (ReadingLevel != ReadingLevel.Focus)
        {
            foreach (var entry in Transcript)
            {
                _ClearGroup(entry);
            }

            return;
        }

        _FormGroups(0, Transcript.Count - 1);
    }

    // Re-forms only the runs a change at [low, high] can touch: a row's group depends on its neighbours and no
    // further, so widening to the enclosing auto-tool run on either side leaves the rest of the transcript alone.
    private void _RegroupAround(int low, int high)
    {
        high = Math.Min(high, Transcript.Count - 1);
        if (low < 0 || high < low)
        {
            return;
        }

        if (ReadingLevel != ReadingLevel.Focus)
        {
            for (var index = low; index <= high; index++)
            {
                _ClearGroup(Transcript[index]);
            }

            return;
        }

        while (low > 0 && Transcript[low - 1].IsAutoTool)
        {
            low--;
        }

        while (high + 1 < Transcript.Count && Transcript[high + 1].IsAutoTool)
        {
            high++;
        }

        _FormGroups(low, high);
    }

    // Walks [start, end] — whose ends are run boundaries — and forms every maximal run of two or more auto tool
    // calls in it into a group, preserving an anchor's expanded state so a run growing mid-turn does not snap shut.
    private void _FormGroups(int start, int end)
    {
        var index = start;
        while (index <= end)
        {
            if (!Transcript[index].IsAutoTool)
            {
                _ClearGroup(Transcript[index]);
                index++;
                continue;
            }

            var runStart = index;
            while (index <= end && Transcript[index].IsAutoTool)
            {
                index++;
            }

            var runLength = index - runStart;
            if (runLength < 2)
            {
                // A lone auto tool call is not worth a fold line — it stays as its own compact chip.
                _ClearGroup(Transcript[runStart]);
                continue;
            }

            var members = new List<TranscriptEntryViewModel>(runLength);
            for (var i = runStart; i < index; i++)
            {
                members.Add(Transcript[i]);
            }

            var expanded = members[0].IsGroupExpanded;
            for (var i = 0; i < members.Count; i++)
            {
                var member = members[i];
                member.IsGroupAnchor = i == 0;
                member.GroupCount = runLength;
                member.IsGroupExpanded = expanded;
                member.GroupToggleRequested = i == 0 ? () => _ToggleGroup(members) : null;
                member.IsInGroup = true;
            }
        }
    }

    private static void _ClearGroup(TranscriptEntryViewModel entry)
    {
        entry.IsInGroup = false;
        entry.IsGroupAnchor = false;
        entry.GroupCount = 0;
        entry.GroupToggleRequested = null;
        // IsGroupExpanded is left as-is: an un-grouped row never reads it, and keeping it makes a later re-fold stable.
    }

    private static void _ToggleGroup(IReadOnlyList<TranscriptEntryViewModel> members)
    {
        var expanded = members.Count > 0 && !members[0].IsGroupExpanded;
        foreach (var member in members)
        {
            member.IsGroupExpanded = expanded;
        }
    }

    // Parameterless constructor kept for the Avalonia previewer design-time context.
    public SessionViewModel(IMentionFileSource? mentionFileSource = null)
    {
        _eventQueue = new SessionEventQueue(Apply);
        _mentionFileSource = mentionFileSource;
        MentionPicker = new MentionPickerViewModel(_MentionPathsAsync, () => WorkingDirectory);
        // Sample MCP selection, and the status line derived from it rather than typed out beside it (AC-563): a
        // hard-coded "Connected (3 MCP servers)." next to an unset selection would have every previewer and render
        // showing a count of three over a hover saying the selection is unknown.
        McpServerSelection = new HashSet<string>(StringComparer.Ordinal) { "youtrack", "depot", "cockpit-local-ci" };
        Status = ConnectedStatusLine;
        ActiveProfileLabel = "raymond@work";
        KindLabel = "SDK";

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
            "run the tests once the build finishes", [], replyTo: null, m => QueuedMessages.Remove(m)));
    }

    public SessionViewModel(
        ISessionManager sessionManager,
        IVoicePushToTalkService? voicePushToTalk = null,
        IVoiceSettingsStore? voiceSettingsStore = null,
        IVoicePlaybackQueue? voicePlaybackQueue = null,
        IOpenMicState? openMicState = null,
        IUsageHistory? usageHistory = null,
        IAgentTurnInboxDelivery? turnInboxDelivery = null,
        SessionStateRecorder? sessionStateRecorder = null,
        IPluginProviderRegistry? pluginProviderRegistry = null,
        VoiceOverlayCoordinator? voiceOverlay = null,
        IProfileLoginChecker? loginChecker = null,
        IProfileLoginStarter? loginStarter = null,
        IMentionFileSource? mentionFileSource = null,
        ISharedUsageCache? sharedUsageCache = null,
        ILogger<SessionViewModel>? logger = null)
        : base(usageHistory)
    {
        _eventQueue = new SessionEventQueue(Apply);
        _sessionManager = sessionManager;
        _turnInboxDelivery = turnInboxDelivery;
        _sessionStateRecorder = sessionStateRecorder;
        _pluginProviderRegistry = pluginProviderRegistry;
        _loginChecker = loginChecker;
        _loginStarter = loginStarter;
        _mentionFileSource = mentionFileSource;
        _sharedUsageCache = sharedUsageCache;
        _logger = logger;
        MentionPicker = new MentionPickerViewModel(_MentionPathsAsync, () => WorkingDirectory);
        _TrackPendingAttachments();
        InitializeVoice(voicePushToTalk, voiceSettingsStore, voicePlaybackQueue, openMicState, voiceOverlay);
        CloseRequested += (_, _) =>
        {
            _loginPollTimer?.Stop();
            _usageCatchUpTimer?.Stop();
        };
    }

    // This is the pane kind turn-start delivery works on (AC-394): the host composes its turns as typed calls on a
    // runtime, so there is a real moment before one goes out to put a peer's message in.
    public override bool DeliversInboxAtTurnStart => _turnInboxDelivery is not null;

    // Use `IsSessionReady` because a driver that never started can leave a runtime accepting sends into nothing.
    public override bool CanTakeAPrompt => IsSessionReady;

    // AC-740: no source registered (design-time/unit-test graph) or no working directory yet both answer empty
    // rather than throw — the picker itself stays closed whenever WorkingDirectory is null (see its own guard),
    // so this only actually runs once a session has one.
    private Task<IReadOnlyList<string>> _MentionPathsAsync(CancellationToken cancellationToken) =>
        _mentionFileSource is not null && WorkingDirectory is { } workingDirectory
            ? _mentionFileSource.GetPathsAsync(workingDirectory, cancellationToken)
            : Task.FromResult<IReadOnlyList<string>>([]);

    private void _TrackPendingAttachments()
    {
        PendingAttachments.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPendingAttachments));
            OnPropertyChanged(nameof(CanSend));
        };
        Transcript.CollectionChanged += (_, e) =>
        {
            OnPropertyChanged(nameof(HasTranscript));
            _OnTranscriptChanged(e);
        };
        QueuedMessages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasQueuedMessages));
        LiveControls.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLiveControls));
    }

    // Keeps the Send button's enabled state in sync as the input text changes (T8 CanSend).
    partial void OnInputTextChanged(string value) => OnPropertyChanged(nameof(CanSend));

    // Starts the session immediately under the profile and options chosen up front in the New-session dialog (#31) —
    // this replaces the old in-panel Start button and inline profile picker.
    public async Task StartConfiguredAsync(SessionProfile profile, PermissionModeOption mode, ModelOption model, EffortOption effort, IReadOnlySet<string>? enabledMcpServerNames = null, string? workingDirectory = null, SessionResume? resume = null, IReadOnlyDictionary<string, string>? launchOptions = null, ReadingLevel? readingLevel = null, IReadOnlyList<string>? preApprovedTools = null, bool preApproveAllTools = false)
    {
        if (_runtime is not null)
        {
            return;
        }

        // AC-410: still set here for a restored pane's first launch — see _restoredOfferSnapshot's own doc for why
        // it has to be captured now rather than read again once the first turn actually completes.
        _restoredOfferSnapshot = RestoreOffer;

        // AC-713: what an auth-related error or the login-poll timer below check against.
        _profile = profile;
        _StartLoginPollTimer();

        // The reading level (AC-138) opens on the per-session override chosen in the New-session dialog, else the
        // profile's default view, else the app default (Developer). The header dropdown can still switch it live.
        ReadingLevel = readingLevel ?? profile?.Defaults?.DefaultReadingLevel ?? ReadingLevel.Developer;

        // For bypass, lock immediately (right after selecting it) so the dropdown shows the single locked "Bypass
        // permissions" entry without a frame where the selection sits outside the bound list.
        var isBypass = mode.Value == SessionOptionCatalog.BypassPermissionModeValue;
        SelectedPermissionMode = mode;
        IsPermissionModeLocked = isBypass;
        SelectedModel = model;
        LiveModelText = model.Value;
        SelectedEffort = effort;
        // AC-537: fold in the profile's own saved selection here (same merge PluginSessionDriverAdapter.StartAsync
        // applies before resolving the registry), so a caller that passed none — but whose profile has one — is not
        // read back as "nothing" for the header.
        McpServerSelection = McpServerRegistryFilter.EffectiveSessionSelection(enabledMcpServerNames, profile?.EnabledMcpServerNames);
        // Pre-authorized tools for a self-driving run (AC-215): auto-allowed in the permission handler below instead
        // of raising a prompt an autonomous run has no one to answer. Empty for an ordinary session.
        _preApprovedTools = preApprovedTools is { Count: > 0 }
            ? new HashSet<string>(preApprovedTools, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        _preApproveAllTools = preApproveAllTools;

        // AC-13: hand the provider this session's own pane id, which its plugin turns into COCKPIT_PANE_ID in the
        // child's environment, so the agent can name its own session to the cockpit-session MCP's set_status tool.
        var mergedOptions = launchOptions is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(launchOptions, StringComparer.OrdinalIgnoreCase);
        mergedOptions[WellKnownPluginSessionOptions.PaneId] = PaneId;
        _launchOptions = mergedOptions;

        // AC-661: the same cap the runtime hands the OS, so the bar can warn on the approach to it.
        MemoryCapBytes = SessionMemoryCap.ResolveBytes(profile, mergedOptions);

        await StartWithProfileAsync(profile, workingDirectory, resume);

        // The runtime is left un-started when the CLI never came up. Unlock and reset the mode so a failed bypass
        // launch doesn't strand the panel on a phantom, disabled "Bypass permissions" with no session.
        if (_runtime is not { IsRunning: true })
        {
            // AC-1239: the quieter half — StartAsync returned without throwing and nothing is running, which used to
            // leave Status reading "Session started." on a session that never did. A launch that threw already spoke.
            // Only with a runtime in hand: a null one means no launch was attempted (the design-time graph) or that a
            // teardown took it mid-start, and neither of those failed to start.
            if (StartFailure is null && _runtime is not null)
            {
                StartFailure = "The provider returned without a running session.";
                _logger?.LogWarning("A session under profile {Profile} is not running after its start.", profile?.Label ?? "(none)");
            }

            if (StartFailure is not null)
            {
                Status = $"Failed to start: {StartFailure}";
            }

            IsPermissionModeLocked = false;
            SelectedPermissionMode = SessionOptionCatalog.DefaultPermissionMode;
        }

        // Refresh the ready-gate (the empty-state's "type to start" prompt) now the launch has settled:
        // true on a live runtime, false when it failed.
        OnPropertyChanged(nameof(IsSessionReady));

        // The one point where this kind's CanTakeAPrompt turns true, so the one point a brief handed over before the
        // runtime existed can go out. Sending it any earlier is what earns the transcript's "The session has not
        // started yet — nothing was sent."; a launch that failed leaves it held rather than sent into nothing.
        DeliverHeldPrompt();
    }

    // A headless stream-json session has no slash-command surface, so a full context could only be escaped by closing
    // the pane and opening another — which also costs the operator the pane's name and its place in the workspace
    // (AC-564).
    public async Task ClearContextAsync(SessionProfile profile)
    {
        if (_runtime is null)
        {
            return;
        }

        // The running turn itself needs nothing here: _StopRuntimeAsync tears down through the runtime, which
        // interrupts before it takes the process away (SessionRuntime.DisposeAsync) — a second interrupt from
        // this side would only be the same call again.
        await _StopRuntimeAsync();

        // The tool never ran, and IsPendingPermission is what the chip and the status actually read (AC-564, AC-529).
        foreach (var pending in Transcript.Where(entry => entry.IsPendingPermission).ToList())
        {
            pending.PermissionDecision = "Cancelled — context cleared";
            pending.IsPendingPermission = false;
        }

        _ResetForNewConversation();

        Transcript.Add(new TranscriptEntryViewModel(
            TranscriptEntryKind.Divider, "Context cleared — a new conversation starts here"));

        // The live selections rather than the launch values: a session whose model or reading level was switched
        // mid-flight carries on as the operator last left it. Pre-approved tools ride along too, so clearing the
        // context of a self-driving run (AC-215) does not quietly demote it into one that stops to ask.
        await StartConfiguredAsync(
            profile,
            SelectedPermissionMode,
            SelectedModel,
            SelectedEffort,
            McpServerSelection,
            WorkingDirectory,
            resume: null,
            _launchOptions,
            ReadingLevel,
            _preApprovedTools.ToList(),
            _preApproveAllTools);
    }

    // Everything that described the conversation just dropped (AC-564). The turn's live state and the queue aimed
    // at it go because the turn they belonged to is over; the numbers go because "ctx 66%" left standing over an
    // empty context is a figure that actively lies (decision 3). The transcript is deliberately not among them.
    private void _ResetForNewConversation()
    {
        QueuedMessages.Clear();
        if (_activeToolCalls.Count > 0)
        {
            _activeToolCalls.Clear();
            _RaiseActiveToolActivityChanged();
        }

        _subAgentLanes.Clear();
        _currentTurnAssistantEntries.Clear();
        _currentAssistantEntry = null;
        _currentOrphanedSubAgentTextEntry = null;
        _readAloudFlushedLength = 0;
        _spokenSomethingThisTurn = false;
        _StopSignOfLifeClock();
        ClearCurrentTurnImages();
        _hasCompletedATurn = false;
        _needsAttention = false;
        IsBusy = false;

        _usage.Reset();
        HasUsage = _usage.HasData;
        UsageSummary = string.Empty;
        UsageTooltip = string.Empty;
        ContextUsedPercent = null;
        RateLimits.Clear();
        LimitsTooltip = string.Empty;
        // AC-761 F1: without this, the next _RefreshLimits() call would merge the old conversation's readings
        // back in and undo the clear above.
        ResetUsageHistory();

        // A restore offer belongs to the conversation this pane was restored with; that conversation is no longer
        // the one running here, so the banner must not go on offering to resume it.
        RestoreOffer = null;
        _RecomputeStatus();
    }

    // The header kind chip's label for a profile's provider (AC-537): a built-in provider's own label, nothing for a
    // plain Claude SDK session (the chip then falls back to "SDK"), and.
    private string _ResolveProviderBadge(SessionProfile? profile)
    {
        if (profile?.Provider is null or SessionProvider.ClaudeCli)
        {
            return string.Empty;
        }

        if (profile.ProviderConfig is not PluginProviderConfig plugin)
        {
            return SessionProviderCatalog.Resolve(profile.Provider).Label;
        }

        var name = _pluginProviderRegistry?.Resolve(plugin.ProviderId)?.DisplayName;
        return string.IsNullOrWhiteSpace(name) ? string.Empty : name;
    }

    private async Task StartWithProfileAsync(SessionProfile? profile, string? workingDirectory = null, SessionResume? resume = null)
    {
        if (_sessionManager is null)
        {
            return;
        }

        ProviderBadge = _ResolveProviderBadge(profile);
        // The shared header's kind chip (AC-37): the provider tag, or "SDK" for a plain Claude SDK session.
        KindLabel = string.IsNullOrEmpty(ProviderBadge) ? "SDK" : ProviderBadge;
        // Set before the launch is even attempted, not after it succeeds (AC-545 follow-up): the label is a fact about
        // what was launched, not about whether it came up, so a launch that throws inside the try below must not leave
        // this session looking never-launched (empty profile) forever.
        ActiveProfileLabel = profile?.Label;

        // A per-session working directory override reflects immediately on the shared base (so the header and
        // the read/observe surface show where this session runs) even before the CLI's own init event confirms
        // its cwd; a blank override leaves it to be filled from that init event as before.
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            WorkingDirectory = workingDirectory;
        }

        IsStarting = true;
        StartFailure = null;
        Status = "Starting...";

        try
        {
            // Inside the try: a profile referencing a missing or unresolvable plugin provider (or an invalid persisted
            // ConfigJson) throws during the runtime's start.
            var runtime = _sessionManager.Create(profile);
            runtime.EventAppended += _OnSessionEvent;
            _runtime = runtime;

            // The session's working life starts here, not when the panel was constructed — whatever the launch
            // waited on (resolving a worktree, a profile) is setup, not work (AC-251).
            _startedAt = DateTimeOffset.Now;

            // The model dropdown lists Claude aliases (opus/sonnet/…), which are meaningless to a local
            // provider — it uses the model set on its profile. Only pass the selected model for Claude, so
            // a local session keeps its own configured model instead of being clobbered with "opus".
            var launchModel = profile?.Provider is null or SessionProvider.ClaudeCli ? SelectedModel.Value : null;
            // AC-218: ProjectId is set on this panel by CockpitViewModel before StartConfiguredAsync runs, so it is
            // already current here — passed through so the driver's MCP fan-out resolves this project's registry view.
            await runtime.StartAsync(profile, SelectedPermissionMode.Value, launchModel, McpServerSelection, workingDirectory, resume, _launchOptions, ProjectId);

            // AC-701: the driver polls usage during StartAsync for every session, resumed or fresh — pull the
            // figures in now rather than leaving the header pill empty until the first turn completes. A driver
            // with nothing yet reads null and _RefreshLimits leaves the bars as they were.
            _RefreshLimits();
            _StartUsageCatchUpTimer();

            // The process the meter weighs (#78) exists only once the driver started it.
            ProcessId = runtime.ProcessId;

            // Capabilities (notably SupportsTools) only settle once the driver has actually started.
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

            // A profile marked "auto-approve tools" (#26) seeds the toggle for a fresh local tool session, so it starts
            // already on instead of needing the operator to flip it every time for a profile they trust.
            var wasAlreadyOn = AutoApproveTools;
            AutoApproveTools = AutoApproveTools || (isLocalToolSession && profile?.Defaults?.AutoApproveTools == true);

            if (AutoApproveTools && wasAlreadyOn)
            {
                await runtime.SetAutoApproveToolsAsync(true);
            }

            // ActiveProfileLabel is already set (before the launch attempt, above); the profile is shown
            // separately from Status, so keep the status itself clean rather than repeating it —
            // "Session started. · personal" read as a duplicate (L6).
            Status = "Session started.";
            // The runtime is up: stop signalling "still starting". IsSessionReady is refreshed by the single
            // caller (StartConfiguredAsync) right after this returns, so it is not raised again here.
            IsStarting = false;

            // Thinking budget has no launch flag — the control request is the only path — so apply
            // the selected effort once the session is live, otherwise it runs at the CLI default
            // until the operator first touches the dropdown.
            await _SetMaxThinkingTokensSafeAsync(SelectedEffort.MaxThinkingTokens);
        }
        catch (Exception ex)
        {
            // AC-1239: recorded and logged, not only written into Status — a failure nothing can read apart from a
            // still-starting session is what made three launches wait out a poll on a session that died in 76 ms.
            StartFailure = ex.Message;
            Status = $"Failed to start: {ex.Message}";
            _logger?.LogWarning(ex, "Starting a session under profile {Profile} ({Provider}) failed.", profile?.Label ?? "(none)", ProviderBadge is { Length: > 0 } ? ProviderBadge : "SDK");
            // The launch failed — clear the "still starting" banner so it does not sit there implying the
            // session is about to come up. IsSessionReady stays false (no running runtime); the caller settles it.
            IsStarting = false;
        }
    }

    // Live-toggles auto-approval of tool calls on the running session's driver (local sessions).
    partial void OnAutoApproveToolsChanged(bool value)
    {
        _ = _runtime?.SetAutoApproveToolsAsync(value);
    }

    // Live-switches the running session's permission mode. No-op before the session has started.
    partial void OnSelectedPermissionModeChanged(PermissionModeOption value)
    {
        if (_runtime is not { IsRunning: true })
        {
            return;
        }

        _ = _SetPermissionModeSafeAsync(value.Value);
    }

    // Live-switches the running session's model. No-op before the session has started.
    partial void OnSelectedModelChanged(ModelOption value)
    {
        if (_runtime is not { IsRunning: true })
        {
            return;
        }

        _ = _SetModelSafeAsync(value.Value);
    }

    // Applies the edited Claude model as a live switch, called by the view when the model field commits (Enter,
    // focus-loss, or picking a suggestion).
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

    // Live-switches the running session's thinking budget. No-op before the session has started.
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
            _interruptRequested = true;

            // AC-943: a turn parked on a permission prompt is answered on the wire by the driver where it can be
            // (Claude, Codex); this sweep is the driver-agnostic half, clearing the row for every driver alike.
            foreach (var pending in Transcript.Where(entry => entry.IsPendingPermission).ToList())
            {
                pending.PermissionDecision = "Cancelled — interrupted";
                pending.IsPendingPermission = false;
            }
        }
        catch (Exception ex)
        {
            Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, $"Interrupt failed: {ex.Message}"));
        }
    }

    // AC-664: asks the provider to summarise this conversation and carry on in it. Reports whether the ask went out,
    // not whether the context shrank — only the provider's next usage reading says that. Marked busy because this is
    // a real turn like any other (`_StartTurnAsync`), and the turn-completed event clears it the same way.
    public async Task<bool> CompactContextAsync()
    {
        if (_runtime is not { IsRunning: true } || !Capabilities.SupportsContextCompaction)
        {
            return false;
        }

        IsBusy = true;
        _RecomputeStatus();

        try
        {
            await _runtime.CompactContextAsync();
            return true;
        }
        catch (Exception ex)
        {
            // The turn never left, so the session is not working — left standing, it would read as permanently busy.
            IsBusy = false;
            _RecomputeStatus();
            Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, $"Compacting the context failed: {ex.Message}"));
            return false;
        }
    }

    // AC-782: the bar's own Compact button, beside Dismiss on the context-fill line. Guarded on the same `IsBusy`
    // the automatic 80%-trigger already checks before it asks (`AssistantSessionHost.ShouldHandOver`), so a click
    // during an in-flight turn — automatic or a second click on this one — does nothing instead of asking twice.
    [RelayCommand(CanExecute = nameof(_CanCompact))]
    private async Task CompactAsync() => await CompactContextAsync();

    private bool _CanCompact => !IsBusy;

    private async Task _SetPermissionModeSafeAsync(string mode)
    {
        if (_runtime is null)
        {
            return;
        }

        try
        {
            await _runtime.SetPermissionModeAsync(mode);

            // AC-409: only once the switch actually took — a failed one leaves the running session on its old
            // mode, and recording the requested one anyway would tell a restart to bring back a mode this session
            // never ran under.
            _ = _sessionStateRecorder?.RecordPermissionModeChangedAsync(PaneId, mode);
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

    // Rebuilds the generic live-control panel from the running driver's declared options (#45 D4).
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

    // Live-switches one of the provider's generic controls on the running session's driver (#45 D4).
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

    // Called from the view's CTRL+V handler, which owns the Avalonia clipboard read; the view model only sees PNG bytes
    // so it stays free of UI-toolkit types and unit-testable.
    public void AddPastedImage(byte[] pngBytes)
    {
        if (!CanPasteImages)
        {
            Transcript.Add(new TranscriptEntryViewModel(
                TranscriptEntryKind.Error, "This session's provider does not support image input — the pasted image was not attached."));
            return;
        }

        PendingAttachments.Add(new ImageAttachmentViewModel(pngBytes, _RemovePendingAttachment));
    }

    // The single per-chip removal path: drop the attachment from the list, then free its decoded
    // bitmap. Disposal happens here (genuine removal) and in the send-path Clear — never on a mere
    // reorder or from a getter, which would blank the still-visible thumbnail.
    private void _RemovePendingAttachment(ImageAttachmentViewModel attachment)
    {
        PendingAttachments.Remove(attachment);
        attachment.Dispose();
    }

    // Empties the pending list on send, freeing each decoded thumbnail as its chip leaves the UI rather
    // than waiting on the GC finalizer. Called only from the send path, once the wire images have already
    // been copied out of PngBytes — never while a chip is still shown.
    private void _ClearPendingAttachments()
    {
        foreach (var attachment in PendingAttachments)
        {
            attachment.Dispose();
        }

        PendingAttachments.Clear();
    }

    // Appends a finished voice transcript to the input box rather than sending it straight away, so
    // the operator can proofread the STT/cleanup result before pressing Enter — the SDK session
    // already has a text input surface, so this reuses it instead of adding a separate send path.
    protected override void OnVoiceTextReady(string text) =>
        InputText = string.IsNullOrEmpty(InputText) ? text : $"{InputText} {text}";

    // Queues a captured screenshot (AC-220) as a pending attachment, the same chip a CTRL+V paste produces — so the
    // operator can type a sentence with it and send when they mean to, rather than the image being shot off on its own.
    protected override Task<string?> OnScreenshotCapturedAsync(byte[] screenshotPng)
    {
        PendingAttachments.Add(new ImageAttachmentViewModel(screenshotPng, _RemovePendingAttachment));
        return Task.FromResult<string?>(null);
    }

    // A provider that never builds an image block would take the attachment and leave without it — so the button is off and the key says why.
    protected override string? ScreenshotKindRefusal =>
        CanPasteImages ? null : "This session's provider does not support image input, so the screenshot was not attached.";

    // Auto-submit: sends the input box the transcript was just appended to — the same path Enter/Send takes, so a busy session queues it (T8) rather than erroring.
    protected override void OnVoiceSubmitRequested()
    {
        if (SendCommand.CanExecute(null))
        {
            SendCommand.Execute(null);
        }
    }

    // Shows a verify screenshot (AC-86) as a real user turn, captioned, only when this provider can see images
    // (`CanPasteImages`) — the same vision gate a pasted image passes through; the text snapshot already reached the
    // agent on the tool result.
    public override async Task<bool> FeedVerifyResultAsync(string caption, byte[] screenshotPng)
    {
        if (_runtime is not { IsRunning: true } || !CanPasteImages)
        {
            return false;
        }

        IReadOnlyList<Core.Sessions.ImageAttachment> images = [Core.Sessions.ImageAttachment.FromBytes(screenshotPng, "image/png")];

        if (IsBusy)
        {
            QueuedMessages.Add(new QueuedMessageViewModel(caption, images, replyTo: null, message => QueuedMessages.Remove(message)));
            return true;
        }

        await _DispatchMessageAsync(caption, images);
        return true;
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) && PendingAttachments.Count == 0)
        {
            return;
        }

        // Sending before the session has started reaches the CLI process before its I/O is wired and surfaces a raw
        // "Start must be called before I/O" error (#16).
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
        var replyTo = PendingReplyTo;

        InputText = string.Empty;
        // The wire images are already copied from PngBytes above, so the decoded thumbnails are done.
        _ClearPendingAttachments();
        PendingReplyTo = null;

        // AC-739: measured 3x that the CLI delivers a mid-turn message to the model. SupportsMidTurnInput gates
        // straight-through writing versus the local send-queue chip (T8), driver by driver.
        if (IsBusy && !Capabilities.SupportsMidTurnInput)
        {
            QueuedMessages.Add(new QueuedMessageViewModel(text, images, replyTo, m => QueuedMessages.Remove(m)));
            return;
        }

        await _DispatchMessageAsync(text, images, replyTo);
    }

    // Pulls the most recently queued message back into the input for editing (Arrow Up on an empty
    // input) — its text and any images are restored and the chip is removed. Returns false when the
    // queue is empty, so the key handler can let Arrow Up do its normal thing.
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

    // Sends a message to the session now, echoing it into the transcript and marking the turn busy. `replyTo`
    // (AC-935) rides only as far as the wire text and the row's own reference — `_lastDispatchedUserTurn` and the
    // "exit" check below stay on the bare `text`, so a reply prefix never changes retry or auto-close behaviour.
    private async Task _DispatchMessageAsync(
        string text, IReadOnlyList<Core.Sessions.ImageAttachment> images, TranscriptEntryViewModel? replyTo = null)
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

        // AC-778: the images ride along on the row itself (not just a "[+N image]" suffix baked into the text)
        // so the row's own chip can reopen them later in this same running session.
        var row = new TranscriptEntryViewModel(TranscriptEntryKind.UserText, text)
        {
            Images = images.Count == 0 ? null : images,
            ReplyTo = replyTo,
        };
        Transcript.Add(row);

        // AC-935: the target's own "answered" marker points at the row that just replied to it, so a click on
        // it jumps straight there — set after the row exists, since that is the reference it points to.
        if (replyTo is not null)
        {
            replyTo.LatestReply = row;
        }

        _lastDispatchedUserTurn = (text, images);
        // AC-1031: a stale flag from a Stop whose own TurnCompleted never arrived (crash, or the interrupt
        // landing after that turn's TurnCompleted already ran) must not paint this new turn's failure as one.
        _interruptRequested = false;
        _currentAssistantEntry = null;
        _CloseThinkingRow();
        IsBusy = true;
        _needsAttention = false;
        _RecomputeStatus();

        // Cleared when the turn completes, or here if the send never happened (AC-116).
        _RememberTurnImages(images);

        try
        {
            await _SendWithWaitingMessagesAsync(_runtime, BuildOutgoingText(text, replyTo), images, _NoteDeliveredMail);
        }
        catch (Exception ex)
        {
            ClearCurrentTurnImages();
            Transcript.Add(new TranscriptEntryViewModel(
                TranscriptEntryKind.Error, SendFailureMessage(ex, _runtime is { IsRunning: true })));
            IsBusy = false;
            _RecomputeStatus();
        }
    }

    // AC-935: the only difference between the wire text and the row is this prefix — same "model sees ≠ row
    // shows" split `_NoteDeliveredMail`'s inbox notice already relies on. Unchanged with no reply target, so an
    // ordinary message costs no extra tokens. Internal so the format can be asserted directly.
    internal static string BuildOutgoingText(string text, TranscriptEntryViewModel? replyTo) =>
        replyTo is null
            ? text
            : $"[reply to \"{TranscriptEntryViewModel.BuildReplyExcerpt(replyTo.TextWithImageSuffix)}\"]: {text}";

    // AC-693: a write into a dead process's stdin says "The pipe is being closed."; the runtime notices that death a
    // beat later than the write does, so both the exception and the flag are read. Internal so the rule can be asserted.
    internal static string SendFailureMessage(Exception exception, bool runtimeIsRunning) =>
        exception is IOException || !runtimeIsRunning
            ? "This session's process has stopped, so the message was not sent."
            : $"Send failed: {exception.Message}";

    // The one place this pane hands a turn to its runtime, so that turn-start delivery (AC-394) cannot be reached by
    // one send path and missed by another — `SessionViewModelSendPathTests` holds it to that.
    private async Task _SendWithWaitingMessagesAsync(
        ISessionRuntime runtime,
        string text,
        IReadOnlyList<Core.Sessions.ImageAttachment>? images,
        Action<AgentInboxTurnNotice>? note = null)
    {
        // Only a runtime that is actually running can carry a turn, and "did not throw" is not enough to tell.
        var waiting = runtime.IsRunning ? _turnInboxDelivery?.TakeForTurn(PaneId) : null;

        try
        {
            // A throw between the taking and the try would leave them held for the life of the pane — counted against
            // its inbox cap, invisible to read_inbox, and freed only when the session closes.
            var outgoing = waiting is null ? text : $"{waiting.Render()}\n\n{text}";
            await runtime.SendUserMessageAsync(outgoing, images);
        }
        catch
        {
            // The turn never left, so neither did the mail. Put it back before the failure travels on, or a send that
            // failed would have quietly consumed messages the recipient never saw and the sender was told had
            // arrived — the one outcome this line is built to prevent.
            if (waiting is not null)
            {
                _turnInboxDelivery?.ReturnUndelivered(waiting);
            }

            throw;
        }

        if (waiting is not null)
        {
            note?.Invoke(waiting);
            _turnInboxDelivery?.ConfirmDelivered(waiting);
        }
    }

    // It exists because this is the first text that enters a session's context which the operator neither typed nor can
    // see (AC-394).
    private void _NoteDeliveredMail(AgentInboxTurnNotice notice)
    {
        var senders = notice.Messages
            .Select(message => message.FromPaneId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var from = senders.Count == 1 ? senders[0] : $"{senders.Count} other sessions";
        var count = notice.Messages.Count == 1 ? "1 message" : $"{notice.Messages.Count} messages";

        Transcript.Add(new TranscriptEntryViewModel(
            TranscriptEntryKind.UserText,
            $"[{count} from {from} delivered with this turn]"));
    }

    // Records the message's images as the current turn's images (AC-116), provider-agnostic, for the read/observe
    // surface to hand to a plugin that reacts to a tool call this turn. A no-op with no images.
    private void _RememberTurnImages(IReadOnlyList<Core.Sessions.ImageAttachment> images)
    {
        if (images.Count == 0)
        {
            return;
        }

        var attachments = images
            .Select((image, index) => new SessionImageAttachment(
                image.MediaType,
                image.Base64Data,
                $"pasted-image-{index + 1}.{_ImageExtension(image.MediaType)}"))
            .ToList();

        SetCurrentTurnImages(attachments);
    }

    private static string _ImageExtension(string mediaType)
    {
        var subtype = mediaType.Split('/').LastOrDefault() ?? string.Empty;
        // A compound subtype (image/svg+xml) or one with parameters (…;charset=…) must not leak "+xml"/";…" into
        // the file name.
        var clean = subtype.Split('+', ';')[0].Trim();

        return clean.Length > 0 ? clean : "png";
    }

    // Dispatches the next queued message (T8) once a turn frees the session. Fire-and-forget: the
    // synchronous part of the dispatch flips `IsBusy` back on before the first await, so
    // the status settles immediately. No-op when the queue is empty.
    private void _TryDispatchNextQueued()
    {
        if (QueuedMessages.Count == 0)
        {
            return;
        }

        // Combine mode (AC-145): drain the whole queue into one follow-up turn so the agent sees every queued message
        // at once, instead of answering each as its own turn.
        if (CombineQueuedMessages && QueuedMessages.Count > 1)
        {
            // AC-935: each sub-message gets its own prefix — one prefix over the whole merged blob would
            // misattribute every message but the first, and several distinct targets cannot collapse into the
            // merged row's one ReplyTo reference, so only the wire text below carries the relation.
            var combinedText = string.Join(
                "\n\n",
                QueuedMessages
                    .Select(m => BuildOutgoingText(m.Text, m.ReplyTo))
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
            var combinedImages = QueuedMessages.SelectMany(m => m.Images).ToList();
            QueuedMessages.Clear();
            _ = _DispatchMessageAsync(combinedText, combinedImages);
            return;
        }

        var next = QueuedMessages[0];
        QueuedMessages.RemoveAt(0);
        _ = _DispatchMessageAsync(next.Text, next.Images, next.ReplyTo);
    }

    // The clarifying-question tool, which arrives over the permission callback like any other tool but wants an
    // answer rather than consent (AC-715). Named the same on both providers that send one: Claude's own tool, and
    // Kimi's, which tunnels it over ACP under this title.
    private const string AskUserQuestionToolName = "AskUserQuestion";

    [RelayCommand]
    private async Task AllowToolAsync(TranscriptEntryViewModel entry)
    {
        await RespondToPermissionAsync(entry, allow: true);
    }

    // Sends the operator's picks back (AC-955). Two routes, split on how the card arrived: a permission-driven
    // AskUserQuestion (AC-715) answers through the tool's own input — an allow without it leaves the agent
    // waiting forever. The assistant's own broker has no such callback, so it takes the typed-message path.
    [RelayCommand]
    private async Task SubmitQuestionAnswersAsync(TranscriptEntryViewModel entry)
    {
        if (!entry.CanSubmitAnswers || entry.QuestionPrompts is not { Count: > 0 } prompts)
        {
            return;
        }

        foreach (var prompt in prompts)
        {
            prompt.IsAnswered = true;
        }

        if (entry.IsPendingBrokerAnswer)
        {
            entry.IsPendingBrokerAnswer = false;
            InjectAndSubmit(_BuildBrokerAnswerText(prompts[0]));
            return;
        }

        var answers = new JsonObject();
        foreach (var prompt in prompts)
        {
            answers[prompt.Question] = prompt.Answer;
        }

        await RespondToPermissionAsync(entry, allow: true, answers.ToJsonString());
    }

    // The wire format for a broker question's answer: spelled out because the model never sees the card itself,
    // and a bare label on its own could otherwise read as a fresh instruction rather than an answer to this one.
    private static string _BuildBrokerAnswerText(AskUserQuestionViewModel prompt) =>
        $"\"{prompt.Question}\" → {prompt.Answer}";

    [RelayCommand]
    private async Task DenyToolAsync(TranscriptEntryViewModel entry)
    {
        await RespondToPermissionAsync(entry, allow: false);
    }

    // Allows the call and persists a rule matching only this exact tool + input for the session's profile.
    [RelayCommand]
    private async Task AllowAlwaysExactToolAsync(TranscriptEntryViewModel entry)
    {
        await AllowAlwaysAsync(entry, PermissionRuleScope.Exact);
    }

    // Allows the call and persists a rule matching every future call to this tool for the session's profile.
    [RelayCommand]
    private async Task AllowAlwaysWildcardToolAsync(TranscriptEntryViewModel entry)
    {
        await AllowAlwaysAsync(entry, PermissionRuleScope.Wildcard);
    }

    private async Task RespondToPermissionAsync(TranscriptEntryViewModel entry, bool allow, string? answersJson = null)
    {
        if (_runtime is null || entry.ToolUseId is null)
        {
            return;
        }

        entry.PermissionDecision = answersJson is not null ? "Answered" : allow ? "Allowed" : "Denied";
        entry.IsPendingPermission = false;
        // AC-532: the operator's decision may be what the composer's activity band was showing "waiting for
        // permission" for — re-raise so it reverts to the normal running text (or goes quiet, if this was the
        // call's only reason to still be shown).
        _RaiseActiveToolActivityChanged();
        await _runtime.RespondToPermissionAsync(entry.ToolUseId, allow, answersJson, CancellationToken.None);
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
        // AC-532: see RespondToPermissionAsync above — this decision can end the composer's "waiting for
        // permission" text too.
        _RaiseActiveToolActivityChanged();

        await _runtime.AllowPermissionAlwaysAsync(entry.ToolUseId, entry.ToolName, entry.InputJson ?? "{}", scope);
    }

    // Called both when the turn finishes and when it pauses on a question/permission prompt mid-turn — so the lead-in a
    // reply gives before asking ("let me check…") is spoken right away instead of staying silent until the operator
    // answers (AC-97).
    private void _FlushPendingProseForReadAloud()
    {
        if (!ReadResponsesAloud)
        {
            return;
        }

        // Only the last entry grows (deltas append to the current one), so the prose up to _readAloudFlushedLength
        // is stable and the tail from there is exactly what has not been spoken yet.
        var prose = string.Join("\n\n", _currentTurnAssistantEntries.Select(entry => entry.Text));
        if (_readAloudFlushedLength >= prose.Length)
        {
            return;
        }

        var pending = prose[_readAloudFlushedLength..];
        _readAloudFlushedLength = prose.Length;
        _spokenSomethingThisTurn = true;
        _RestartSignOfLifeClock();
        _ = EnqueueReadAloudAsync(pending);
    }

    // Says out loud that it is about to go and look, when the model went straight to a tool without saying so (AC-597).
    internal void _SpeakLeadInIfTheModelGaveNone()
    {
        if (!ReadResponsesAloud || _spokenSomethingThisTurn || !IsTheVoiceAssistant)
        {
            return;
        }

        var filler = AssistantSpokenFillers.GoingToLookUpSomething(ReadAloudLanguage, _spokenFillerRotation++);
        if (filler.Length == 0)
        {
            return;
        }

        _spokenSomethingThisTurn = true;
        _RestartSignOfLifeClock();
        _ = EnqueueReadAloudAsync(filler);
    }

    // True for the cockpit's own voice assistant, the one session that speaks unasked (AC-597/598).
    private bool IsTheVoiceAssistant =>
        string.Equals(PaneId, AssistantIdentity.PaneId, StringComparison.Ordinal);

    // Starts the clock that says "still on it" while a turn keeps running (AC-598), and pushes it back every time
    // something real is spoken.
    private void _RestartSignOfLifeClock()
    {
        if (!ReadResponsesAloud || !IsTheVoiceAssistant)
        {
            return;
        }

        _signOfLifeTimer ??= _BuildSignOfLifeTimer();
        _signOfLifeTimer.Stop();
        _signOfLifeTimer.Interval = AssistantSpokenFillers.SignOfLifeDelay(_signOfLifeRepeat);
        _signOfLifeTimer.Start();
    }

    private DispatcherTimer _BuildSignOfLifeTimer()
    {
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) =>
        {
            // A turn that ended, or one stopped on a permission: the first has nothing to report and the second
            // already said out loud that it is waiting. Speaking over either is noise.
            if (!IsBusy || HasPendingPermission || PendingConsent is not null)
            {
                _StopSignOfLifeClock();
                return;
            }

            var filler = AssistantSpokenFillers.StillAtIt(ReadAloudLanguage, _signOfLifeRepeat);
            _signOfLifeRepeat++;
            if (filler.Length > 0)
            {
                _ = EnqueueReadAloudAsync(filler);
            }

            timer.Interval = AssistantSpokenFillers.SignOfLifeDelay(_signOfLifeRepeat);
        };

        return timer;
    }

    private void _StopSignOfLifeClock()
    {
        _signOfLifeTimer?.Stop();
        _signOfLifeRepeat = 0;
    }

    // The runtime pumps the driver off the UI thread and raises each event here (#68); marshalling onto the UI thread
    // is this panel's job, because it is the consumer that touches UI — a headless consumer of the same runtime
    // marshals nothing (AC-529).
    private void _OnSessionEvent(SessionEvent evt) => _eventQueue.Enqueue(evt);

    private readonly SessionEventQueue _eventQueue;

    // Raised when the session makes real tool progress — a tool call surfacing or a tool result landing (AC-215/stall).
    // An embedder that fails a silent step on a stall deadline (Autopilot) resets that deadline on this, so a step that
    // is slow because it is working hard is not mistaken for a stuck one. Not raised on text/thinking on purpose.
    public event Action? ToolActivity;

    // internal (rather than private) so `Cockpit.Core.Tests` can drive it directly, bypassing `Dispatcher.UIThread` — see `_OnSessionEvent`.
    internal void Apply(SessionEvent evt)
    {
        // It used to track "no visible output yet" — cleared by the first text, re-armed only by a ToolResult — and
        // that is what left the composer blank for a minute at a time when the model said something and then went back
        // to work (AC-532).

        // Real tool progress (AC-215/stall): a tool call surfacing or a tool result landing is the agent actually
        // working — the signal that distinguishes a busy-but-progressing step from a genuinely stuck one (AC-192: a
        // turn that emits text describing a tool it never runs, so no tool event ever fires).
        if (evt is ToolUseRequested or ToolResult)
        {
            ToolActivity?.Invoke();
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

                // AC-537: the tool count said nothing an operator could act on, and cwd duplicated the folder icon's
                // own tooltip (SessionHeaderBar.axaml) (AC-563).
                Status = ConnectedStatusLine;
                // AC-563 took the tool names off the provider chip's hover — the same count AC-537 had already ruled
                // uninformative, one hover further along.
                ConnectedToolsHeading = init.Tools.Count == 0
                    ? "No tools connected — add an MCP server (e.g. filesystem) to give this session tools."
                    : $"{init.Tools.Count} tools connected";

                // AC-963: the same hover that lists the servers now says what became of their tools — preloaded, or
                // kept out of the prompt behind search_tools. Only the init event knows which of the two happened.
                McpToolReach = McpToolReachFor(init.Tools);

                // Seed it in, don't fire a switch: the driver already reported this, and set_model would be the host
                // talking back a choice the operator never made (AC-141).
                if (init.Model is { Length: > 0 } resolvedModel)
                {
                    foreach (var control in LiveControls)
                    {
                        if (control.Key == WellKnownPluginSessionOptions.Model)
                        {
                            control.SeedIfUnset(resolvedModel);
                        }
                    }
                }

                break;

            case AssistantTextDelta delta:
                // AC-146: a sub-agent's own streaming text accumulates onto its lane's row, nested under its
                // Task tool-use anchor rather than into the top-level transcript — the operator's own reply and
                // a sub-agent's internal narration must never merge into one row.
                if (_ResolveSubAgentLane(delta.ParentToolUseId) is { } textLane)
                {
                    textLane.CurrentThinkingEntry = null;
                    if (textLane.CurrentAssistantEntry is null)
                    {
                        textLane.CurrentAssistantEntry = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, string.Empty);
                        textLane.Anchor.SubAgentRows.Add(textLane.CurrentAssistantEntry);
                    }

                    textLane.CurrentAssistantEntry.AppendText(delta.Text);
                    break;
                }

                // AC-146: a parent id this pane never resolved to a lane (the anchor tool-use row was never
                // seen) is an orphan, not a top-level chunk — shown, in its own separate entry so it can never
                // merge into whatever the genuine top-level reply is doing, but never queued for read-aloud.
                if (!string.IsNullOrEmpty(delta.ParentToolUseId))
                {
                    if (_currentOrphanedSubAgentTextEntry is null)
                    {
                        _currentOrphanedSubAgentTextEntry = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, string.Empty);
                        Transcript.Add(_currentOrphanedSubAgentTextEntry);
                    }

                    _currentOrphanedSubAgentTextEntry.AppendText(delta.Text);
                    break;
                }

                // Visible prose has started, so the reasoning block that preceded it is done: close the thinking
                // row (AC-213) so a later thinking block opens a fresh row instead of appending onto this one.
                _CloseThinkingRow();
                if (_currentAssistantEntry is null)
                {
                    _currentAssistantEntry = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, string.Empty);
                    Transcript.Add(_currentAssistantEntry);
                    _currentTurnAssistantEntries.Add(_currentAssistantEntry);
                }

                _currentAssistantEntry.AppendText(delta.Text);
                break;

            case AssistantTextCompleted completed:
                // A sub-agent's completed-text snapshot (some providers send one instead of streaming deltas)
                // lands on its own lane the same way the streaming path above does, and never joins the
                // top-level read-aloud queue below.
                if (_ResolveSubAgentLane(completed.ParentToolUseId) is { } completedLane)
                {
                    completedLane.CurrentThinkingEntry = null;
                    if (completedLane.CurrentAssistantEntry is not null)
                    {
                        completedLane.CurrentAssistantEntry = null;
                    }
                    else
                    {
                        completedLane.Anchor.SubAgentRows.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, completed.Text));
                    }

                    break;
                }

                // AC-146: same orphan handling as the streaming case above — never queued for read-aloud, never
                // raises the output-text signal below.
                if (!string.IsNullOrEmpty(completed.ParentToolUseId))
                {
                    if (_currentOrphanedSubAgentTextEntry is not null)
                    {
                        _currentOrphanedSubAgentTextEntry = null;
                    }
                    else
                    {
                        Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, completed.Text));
                    }

                    break;
                }

                _CloseThinkingRow();
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

                // A sub-agent's own narration is not the session's answer to the operator, so it never reaches this
                // signal either (kept inside the branch above).
                RaiseOutputText(completed.Text);
                break;

            case ToolUseRequested toolUse:
                // AC-146: a sub-agent's own tool call nests under its Task row instead of flattening into the
                // top-level transcript — this is also how a sub-agent's tool call becomes an anchor a *further*
                // nested lane could resolve against, though today's CLI only nests one level deep.
                if (_ResolveSubAgentLane(toolUse.ParentToolUseId) is { } toolUseLane)
                {
                    toolUseLane.CurrentAssistantEntry = null;
                    toolUseLane.CurrentThinkingEntry = null;
                    toolUseLane.Anchor.SubAgentRows.Add(
                        _ToolUseRow(toolUse.ToolUseId, toolUse.ToolName, toolUse.InputJson));
                    break;
                }

                // Close the current assistant text row so prose that streams *after* this tool call starts a
                // fresh row beneath the tool, in the order it happened — otherwise post-tool text appends back
                // onto the pre-tool row and the whole reply collapses above the tools it actually followed.
                _currentAssistantEntry = null;
                _CloseThinkingRow();
                var toolUseRow = _ToolUseRow(toolUse.ToolUseId, toolUse.ToolName, toolUse.InputJson);
                Transcript.Add(toolUseRow);

                // AC-532: this top-level call is now outstanding — reuses the row's own ToolHeader ("Bash  ·
                // dotnet build") rather than re-deriving a summary from the input JSON a second time.
                _activeToolCalls.Add(new ActiveToolCall(toolUse.ToolUseId, toolUseRow.ToolHeader, DateTimeOffset.Now));
                _RaiseActiveToolActivityChanged();

                // Until now the only mid-turn flushes were a permission prompt and a question, which was enough while
                // every tool call raised one — and stopped being enough the moment an operator turned on
                // bypassPermissions or the cockpit's consent bypass (AC-575).
                _FlushPendingProseForReadAloud();

                // AC-597: and when there was no lead-in to flush, say one of our own. Two turns in five reach this
                // line with nothing written yet, and silence from the question to the answer reads as unheard.
                _SpeakLeadInIfTheModelGaveNone();

                // AC-598: the wait starts here too, so a turn that said its lead-in and then spends two minutes in
                // tools still gives a sign of life.
                _RestartSignOfLifeClock();
                break;

            case ToolResult toolResult:
                // AC-146: a sub-agent's own tool result couples to its tool-use row inside that lane's nested
                // rows, the same by-tool_use_id matching the top-level branch below uses — never the flat
                // Transcript, and never the output/tool-activity signals a top-level result raises.
                if (_ResolveSubAgentLane(toolResult.ParentToolUseId) is { } toolResultLane)
                {
                    var nestedToolUseEntry = toolResultLane.Anchor.SubAgentRows.LastOrDefault(
                        t => t.Kind == TranscriptEntryKind.ToolUse && t.ToolUseId == toolResult.ToolUseId);
                    if (nestedToolUseEntry is not null)
                    {
                        nestedToolUseEntry.SetResult(toolResult.Content, toolResult.IsError);
                        _TrackBackgroundToolRow(nestedToolUseEntry);
                    }
                    else
                    {
                        toolResultLane.Anchor.SubAgentRows.Add(new TranscriptEntryViewModel(
                            TranscriptEntryKind.ToolResult,
                            toolResult.IsError ? $"Tool error: {toolResult.Content}" : $"Tool result: {toolResult.Content}"));
                    }

                    break;
                }

                var toolUseEntry = Transcript.LastOrDefault(
                    t => t.Kind == TranscriptEntryKind.ToolUse && t.ToolUseId == toolResult.ToolUseId);
                if (toolUseEntry is not null)
                {
                    // Couple the result to its tool-call row (L14) so it renders as an expandable
                    // section beneath that call, instead of a detached row that loses which call it
                    // belongs to — the pain with parallel tool calls.
                    toolUseEntry.SetResult(toolResult.Content, toolResult.IsError);
                    _TrackBackgroundToolRow(toolUseEntry);
                }
                else
                {
                    // No matching tool-use in view (e.g. a result arriving first): fall back to a row.
                    Transcript.Add(new TranscriptEntryViewModel(
                        TranscriptEntryKind.ToolResult,
                        toolResult.IsError ? $"Tool error: {toolResult.Content}" : $"Tool result: {toolResult.Content}"));
                }

                // AC-532: this call is no longer outstanding, whichever way it resolved — success, error, or a
                // permission denial the driver reported as a tool result.
                var activeCallIndex = _activeToolCalls.FindIndex(call => call.ToolUseId == toolResult.ToolUseId);
                if (activeCallIndex >= 0)
                {
                    _activeToolCalls.RemoveAt(activeCallIndex);
                    _RaiseActiveToolActivityChanged();
                }

                // AC-146: a result naming a parent this pane never resolved to a lane (the anchor tool-use row was
                // never seen) is coupled/shown above like any other, so nothing vanishes silently.
                if (!string.IsNullOrEmpty(toolResult.ParentToolUseId))
                {
                    break;
                }

                // Tool output is where a shelled-out `gh pr create`/`git push` prints its pull-request url, so
                // it is the primary channel the PR watcher scans (the read/observe surface).
                RaiseOutputText(toolResult.Content);

                // And, coupled with its call, the structured tool-activity signal (AC-116): the tool-use row we just
                // found carries the name and input, the result carries the content.
                if (toolUseEntry is { ToolName: { } toolName })
                {
                    RaiseToolActivity(toolName, toolUseEntry.InputJson ?? "{}", toolResult.Content, toolResult.IsError);
                }

                break;

            case PermissionRequested permission:
                // AC-146: a sub-agent's own tool call can need approval too — the row it responds to lives
                // nested under its Task anchor rather than in the flat Transcript, so look there when the
                // top-level search comes up empty.
                var entry = Transcript.LastOrDefault(t => t.ToolUseId == permission.ToolUseId)
                    ?? _ResolveSubAgentLane(permission.ParentToolUseId)?.Anchor.SubAgentRows.LastOrDefault(t => t.ToolUseId == permission.ToolUseId);

                // A pre-authorized tool for a self-driving run (AC-215): auto-allow it here rather than raising a
                // prompt the autonomous run has no one to answer — that stall left the run stuck first on its own
                // autopilot_step_done, then on the Bash its work needs.
                if ((_preApproveAllTools || _preApprovedTools.Contains(permission.ToolName)) && _runtime is not null)
                {
                    if (entry is not null)
                    {
                        entry.PermissionDecision = "Allowed";
                        entry.IsPendingPermission = false;
                    }

                    _ = _runtime.RespondToPermissionAsync(permission.ToolUseId, allow: true);
                    break;
                }

                // AC-996: `_needsAttention` below is unconditional, while the consent card only exists where a row
                // does — so a permission whose tool-use row never arrived parked the session on needs-attention
                // with nothing to click. Give it a row of its own; the event carries all a row needs.
                entry ??= _AddOrphanPermissionRow(permission);

                // AC-715: an AskUserQuestion rides this same callback but asks for an answer, not consent —
                // parse its questions here so the row renders them as choices instead of Allow/Deny over raw
                // JSON. Any other tool parses to nothing and keeps the ordinary consent card.
                entry.QuestionPrompts = permission.ToolName == AskUserQuestionToolName
                    ? AskUserQuestionViewModel.Parse(permission.InputJson)
                    : null;
                entry.IsPendingPermission = true;
                // AC-532: a top-level call stalling on this prompt is why the turn looks idle right now —
                // flip the composer's activity band from "running" to "waiting for permission" so that reads
                // as waiting on the operator rather than as the tool quietly still working.
                _RaiseActiveToolActivityChanged();

                _needsAttention = true;
                // Speak the lead-in the reply gave before this tool needs approval, rather than holding it back
                // until the operator answers the prompt (AC-97).
                _FlushPendingProseForReadAloud();
                _RecomputeStatus();
                break;

            case Question question:
                Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Question, question.Text));
                // Same as a permission prompt: a question pauses the turn, so speak what was said before it now.
                _FlushPendingProseForReadAloud();
                break;

            case TurnCompleted turn:
                // AC-1031: consumed once, right here — a turn's own IsError below must not keep reading this
                // as "interrupted" once we've reported it, or a later genuine failure would render as one too.
                var wasInterrupted = _interruptRequested;
                _interruptRequested = false;

                // Only surface a turn row when it failed — a plain "Turn completed (success)" row is
                // noise in the transcript (T4). The Done status still fires below.
                if (turn.IsError && wasInterrupted)
                {
                    // AC-1031: the operator asked for this stop — it is not a driver failure, so it gets none of
                    // the failure card's severity styling, reason text, or Retry action (AC-720's "Signing in
                    // again…" status row is the existing precedent for a plain TurnCompleted row like this).
                    Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.TurnCompleted, "Interrupted."));
                }
                else if (turn.IsError)
                {
                    // AC-720: the subtype alone ("error_during_execution") names nothing actionable — show
                    // the provider's own reason (AC-410's Errors) when the event carries one.
                    var reason = _TurnFailureReason(turn);
                    // AC-939: classify that reason the same way a driver SessionError does (below), so a
                    // recognised provider outage (e.g. Claude's "API Error: 529 …") renders through the
                    // severity card instead of staying permanently Unknown.
                    var errorKind = reason is null ? SessionErrorKind.Unknown : SessionErrorClassifier.Classify(reason);
                    // AC-939: the subtype is contradictory once it reads "success" on a failed turn, and
                    // redundant once a recognised reason already names what happened — drop it from the
                    // title in both cases instead of always interpolating it.
                    var dropsSubtype = turn.Subtype == "success" || errorKind != SessionErrorKind.Unknown;
                    var title = dropsSubtype ? "Turn failed" : $"Turn failed ({turn.Subtype})";
                    var failedTurnRow = new TranscriptEntryViewModel(
                        TranscriptEntryKind.TurnCompleted,
                        reason is null ? title : $"{title}: {reason}")
                    {
                        // AC-728: renders through the same severity card as a driver SessionError (AC-720) —
                        // a failed turn is as much "a problem" as one.
                        IsFailedTurnRow = true,
                        ErrorKind = errorKind,
                    };

                    // AC-939: an auth classification means Retry would just fail again — offer the same
                    // login-gate the SessionError branch below uses instead.
                    if (errorKind == SessionErrorKind.AuthRequired && _profile is not null && _loginChecker?.IsLoggedIn(_profile) == false)
                    {
                        failedTurnRow.ActionLabel = "Login";
                        failedTurnRow.ActionCommand = new RelayCommand(() => _StartLoginFlow(failedTurnRow));
                    }
                    // AC-728: same ActionLabel/ActionCommand convention as AC-713's "Login" row. Left unset when
                    // this turn never went through _DispatchMessageAsync — a scheduled resume's own first turn
                    // (SendPromptAsync, AC-410) is the one case that applies to.
                    else if (_lastDispatchedUserTurn is { } lastTurn)
                    {
                        failedTurnRow.ActionLabel = "Retry";
                        failedTurnRow.ActionCommand = new RelayCommand(() => _ = _DispatchMessageAsync(lastTurn.Text, lastTurn.Images));
                    }

                    Transcript.Add(failedTurnRow);
                }

                // A failure here is a resume that was actually tried and refused (an expired conversation id makes
                // claude --resume print "No conversation found" and end the turn as error_during_execution with no
                // Result) (AC-410).
                if (_restoredOfferSnapshot is { } restoredOffer)
                {
                    _restoredOfferSnapshot = null;
                    // AC-1031: an interrupted first turn is not a refused resume — leave the offer alone rather
                    // than degrading it to Gone over a stop the operator asked for.
                    if (turn.IsError && !wasInterrupted)
                    {
                        RestoreOffer = restoredOffer with
                        {
                            Availability = SessionRestoreAvailability.Gone,
                            Explanation = _DegradedTurnExplanation(turn, restoredOffer.State?.WorkingDirectory),
                        };
                    }
                }

                _FlushPendingProseForReadAloud();

                _currentTurnAssistantEntries.Clear();
                _readAloudFlushedLength = 0;
                _spokenSomethingThisTurn = false;
                _StopSignOfLifeClock();
                _currentAssistantEntry = null;
                _CloseThinkingRow();
                // A sub-agent does not outlive the turn that spawned it (AC-146): a fresh Task call next turn
                // gets a fresh anchor and lane, never one still holding a finished sub-agent's dangling state.
                _subAgentLanes.Clear();
                _currentOrphanedSubAgentTextEntry = null;
                // This turn's images belong to this turn only (AC-116): drop them so a later image-less turn's
                // tool call attaches nothing stale.
                ClearCurrentTurnImages();
                // AC-532 safety net: every turn ends here or in SessionError below, whether or not each of its tool
                // calls got a matching ToolResult first (an interrupt ends the turn without one).
                if (_activeToolCalls.Count > 0)
                {
                    _activeToolCalls.Clear();
                    _RaiseActiveToolActivityChanged();
                }

                // AC-531: deliberately no _backgroundTasks/_RebuildBackgroundTaskRows() call here, unlike
                // _activeToolCalls just above.
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
                var errorEntry = new TranscriptEntryViewModel(TranscriptEntryKind.Error, error.Message)
                {
                    // AC-720: trust a driver that classified itself; otherwise fall back to the host's text
                    // heuristic so an untyped driver still renders better than a guessed severity.
                    ErrorKind = error.Kind == SessionErrorKind.Unknown
                        ? SessionErrorClassifier.Classify(error.Message)
                        : error.Kind,
                    RetryAfter = error.RetryAfter,
                };
                // AC-713: re-checks the profile's own login gate rather than pattern-matching `error.Message`.
                if (_profile is not null && _loginChecker?.IsLoggedIn(_profile) == false)
                {
                    errorEntry.ActionLabel = "Login";
                    errorEntry.ActionCommand = new RelayCommand(() => _StartLoginFlow(errorEntry));
                }

                Transcript.Add(errorEntry);
                // A session error ends the turn without a TurnCompleted, so drop this turn's images here too —
                // otherwise a later image-less turn's tool call could attach the errored turn's stale images (AC-116).
                ClearCurrentTurnImages();
                IsBusy = false;
                // Whatever was outstanding died with the session (AC-276). Unlike the TTY route this one has no
                // safety timeout to fall back on, so a sub-agent left in the list here would hold a crashed session
                // on WorkingBackground forever — and make closing it ask "still working?" on the way out.
                _backgroundTasks = [];
                OnPropertyChanged(nameof(HasOutstandingBackgroundShells));
                _RebuildBackgroundTaskRows();
                // AC-532: same reasoning as the background-task list above — a crashed driver never sends the
                // ToolResult that would otherwise have cleared this, so the composer must not go on showing a
                // tool as running past the session that was running it.
                if (_activeToolCalls.Count > 0)
                {
                    _activeToolCalls.Clear();
                    _RaiseActiveToolActivityChanged();
                }

                _RecomputeStatus();
                break;

            // The driver restated what is still outstanding (AC-276). Kept verbatim — the weighing of sub-agent
            // versus shell belongs to _RecomputeStatus and the notification gate, not here, so this stays a
            // straight assignment even when the list is empty (which is how the last task ending arrives).
            case BackgroundTasksChanged backgroundTasks:
                _backgroundTasks = backgroundTasks.Tasks;
                OnPropertyChanged(nameof(HasOutstandingBackgroundShells));
                _RebuildBackgroundTaskRows();
                _RecomputeStatus();
                break;

            // AC-1057: the provider's own verdict on one background task, replacing the inferred done/failed guess
            // for exactly the row that started it. Matched by ToolUseId first (the id this event names for that
            // reason) and by BackgroundTaskId as a fallback for a row whose ToolUseId went unset for some reason.
            case BackgroundTaskNotification notification:
                var notifiedRow = notification.ToolUseId is not null
                    ? _backgroundToolRows.FirstOrDefault(row => row.ToolUseId == notification.ToolUseId)
                    : null;
                notifiedRow ??= _backgroundToolRows.FirstOrDefault(row => row.BackgroundTaskId == notification.TaskId);
                if (notifiedRow is not null)
                {
                    notifiedRow.BackgroundNotificationStatus = notification.Status;
                }

                break;

            case SessionStatusChanged statusChanged:
                // A non-empty needs_action requests sidebar attention, like a pending permission (AC-146).
                if (!string.IsNullOrEmpty(statusChanged.NeedsAction))
                {
                    _needsAttention = true;
                }

                _RecomputeStatus();
                break;

            // The row is added at every reading level but only *renders* at Developer — its IsRowVisible gates it off
            // at Focus/Simple, which stay calm (AC-138) (AC-213, AC-144).
            case AssistantThinkingDelta thinkingDelta:
                if (!string.IsNullOrEmpty(thinkingDelta.Thinking))
                {
                    // AC-146: a sub-agent's own reasoning stays in its lane, same rule as its text above.
                    if (_ResolveSubAgentLane(thinkingDelta.ParentToolUseId) is { } thinkingLane)
                    {
                        if (thinkingLane.CurrentThinkingEntry is null || thinkingDelta.BlockIndex != thinkingLane.CurrentThinkingBlockIndex)
                        {
                            thinkingLane.CurrentThinkingEntry = new TranscriptEntryViewModel(TranscriptEntryKind.Thinking, string.Empty)
                            {
                                IsExpanded = true,
                            };
                            thinkingLane.CurrentThinkingBlockIndex = thinkingDelta.BlockIndex;
                            thinkingLane.Anchor.SubAgentRows.Add(thinkingLane.CurrentThinkingEntry);
                        }

                        thinkingLane.CurrentThinkingEntry.AppendText(thinkingDelta.Thinking);
                        break;
                    }

                    if (_currentThinkingEntry is null || thinkingDelta.BlockIndex != _currentThinkingBlockIndex)
                    {
                        _currentThinkingEntry = new TranscriptEntryViewModel(TranscriptEntryKind.Thinking, string.Empty)
                        {
                            IsExpanded = true,
                        };
                        _currentThinkingBlockIndex = thinkingDelta.BlockIndex;
                        Transcript.Add(_currentThinkingEntry);
                    }

                    _currentThinkingEntry.AppendText(thinkingDelta.Thinking);
                }

                break;

            case RateLimitInfo:
            case UnknownEvent:
                break;
        }
    }

    // Ends the currently-streaming reasoning row (AC-213) so the next thinking block, or the next turn, opens a
    // fresh row instead of appending onto a stale one. Called wherever the assistant text row is likewise reset.
    private void _CloseThinkingRow()
    {
        _currentThinkingEntry = null;
        _currentThinkingBlockIndex = -1;
    }

    // Derives `SessionStatus` from the flags this view model already tracks: busy while a turn is in flight; see
    // AC-276.
    private void _RecomputeStatus()
    {
        SessionStatus = (_needsAttention, IsBusy, _HasOutstandingSubAgents) switch
        {
            (true, _, _) => SessionStatus.NeedsAttention,
            (false, true, _) => SessionStatus.Busy,
            (false, false, true) => SessionStatus.WorkingBackground,
            (false, false, false) => _hasCompletedATurn ? SessionStatus.Done : SessionStatus.Idle,
        };
    }

    // Replaced wholesale rather than added to and removed from: the event carries the complete set every time (see
    // `BackgroundTasksChanged`), so a dropped event costs one stale reading instead of permanently desynchronising a
    // ledger.
    private IReadOnlyList<BackgroundTask> _backgroundTasks = [];

    private bool _HasOutstandingSubAgents => _backgroundTasks.Any(task => task.Kind == BackgroundTaskKind.SubAgent);

    // True while a backgrounded shell is still running (AC-276). It does not hold the status — a never-ending
    // dev server would pin the session forever — but it does suppress the "session finished" notification, which
    // would otherwise announce a session that is still doing something.
    public override bool HasOutstandingBackgroundShells => _backgroundTasks.Any(task => task.Kind == BackgroundTaskKind.Shell);

    // The meter sums the tokens and follows the cost, which the result reports as a session total rather than a
    // per-turn share.
    internal void _AccumulateUsage(TurnCompleted turn)
    {
        _usage.Add(turn.Usage, turn.TotalCostUsd);
        HasUsage = _usage.HasData;
        UsageSummary = _usage.Summary;
        UsageTooltip = _usage.Tooltip;
        _RecordUsageSnapshot();
    }

    // Write the running totals to the usage trail after every turn (AC-251), so they outlive the session and the app —
    // recording only at the end would lose exactly the run that crashed, which is the case worth measuring.
    private protected override (UsageRunKind RunKind, string? RunId, string? RunLabel, string? Model) GetUsageSnapshotMetadata() =>
        (RunKind, RunId, RunLabel, SelectedModel.Value);

    public override async Task<bool> SendPromptAsync(string prompt)
    {
        // A runtime whose driver never came up is still held by the pane, and it accepts a send and hands back a
        // completed task with nothing having gone anywhere.
        if (_runtime is not { IsRunning: true } runtime)
        {
            return false;
        }

        // A turn started from here is as real as one the operator typed, and the rest of the cockpit only learns that
        // from these flags: the composer queues behind IsBusy rather than sending on top of a running turn, and
        // AC-395's wake refuses a pane that is already working.
        IsBusy = true;
        _RecomputeStatus();

        try
        {
            // Through the same funnel as the composer's own sends: a scheduled resume is a real turn on a real session,
            // so mail waiting for this pane belongs on it just as much. Routing it around the funnel is exactly the kind
            // of second path that leaves one route delivering and the other not.
            await _SendWithWaitingMessagesAsync(runtime, prompt, images: null);
        }
        catch
        {
            // The turn never left, so the session is not working — left standing, it would read as permanently busy:
            // the composer would queue forever and no later message could ever wake it. Rethrown rather than swallowed,
            // because the callers already decide what a failed prompt means for them.
            IsBusy = false;
            _RecomputeStatus();
            throw;
        }

        return true;
    }

    // AC-539: that reason names the id but not what decides whether it can be found — Claude keeps its saved
    // conversations per working directory, so a pane that came back somewhere else gets the message with nothing
    // pointing at the cause (AC-410).
    private static string _DegradedTurnExplanation(TurnCompleted turn, string? workingDirectory)
    {
        var reason = _TurnFailureReason(turn) ?? $"Claude could not resume the earlier conversation ({turn.Subtype}).";

        return workingDirectory is { Length: > 0 } directory
            ? $"{reason}\nThe resume was made in {directory} — Claude keeps its conversations per working directory, so one saved elsewhere is not found here."
            : reason;
    }

    // The provider's own reason a turn failed (AC-410), when it gave one — null otherwise.
    private static string? _TurnFailureReason(TurnCompleted turn) =>
        turn.Errors is { Count: > 0 } errors ? string.Join('\n', errors) : null;

    // --- Login flow (AC-713) ----------------------------------------------------------------------------------

    // "Sign in again" on the panel-wide auth-expiry bar: unlike the reactive row (below), there is no existing
    // row to expand into — the mockup's own answer is to open one, so there is still exactly one place a login
    // flow ever plays out, regardless of where it started.
    protected override void OnSignInAgainRequested()
    {
        // AC-720: TurnCompleted, not Error — this is a status line, not a driver failure, and Error rows
        // now render as a severity-coloured card that would misread "Signing in again…" as a problem.
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.TurnCompleted, "Signing in again…");
        Transcript.Add(entry);
        _StartLoginFlow(entry);
    }

    // Starts an in-app login attempt and shows it inline on `entry`, replacing whatever action button asked for
    // it (`TranscriptEntryViewModel.HasAction` hides itself once `LoginFlow` is set).
    private void _StartLoginFlow(TranscriptEntryViewModel entry)
    {
        if (_profile is null || _loginStarter?.StartLogin(_profile, CancellationToken.None) is not { } flow)
        {
            return;
        }

        var loginFlow = new LoginFlowRowViewModel(flow);
        // A success is the CLI itself just reporting it — clear the bar now rather than wait for the poll's own
        // next tick (up to a minute away) to re-read a cache this flow just made stale.
        loginFlow.Completed = succeeded =>
        {
            if (succeeded)
            {
                ReportLoginStatus(true);
            }
        };
        entry.LoginFlow = loginFlow;
    }

    // Polls the profile's login gate for the auth-expiry bar. Same interval as `ClaudeLoginStatus.MaxAge`
    // (1 minute): a tick mostly just re-reads a cache another poll already refreshed, rather than forcing its own
    // subprocess every time.
    private void _StartLoginPollTimer()
    {
        if (_loginChecker is null || _profile is null)
        {
            return;
        }

        // AC-564: only subscribe `Tick` for a genuinely new timer, since `ClearContextAsync` re-runs this on the same instance.
        if (_loginPollTimer is null)
        {
            _loginPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _loginPollTimer.Tick += (_, _) => ReportLoginStatus(_loginChecker.IsLoggedIn(_profile));
        }

        ReportLoginStatus(_loginChecker.IsLoggedIn(_profile));
        _loginPollTimer.Start();
    }

    // AC-761: fallback signal for a profile with no registered plugin provider (an unmigrated legacy Claude
    // profile) — the same numbers ClaudeUsageSignals declares, so it still gets a threshold instead of none.
    private static readonly PluginUsageSignal _FallbackContextSignal =
        new("context", "ctx", PluginUsageSignalKind.Fill, DefaultThresholdPercent: 50) { Description = "Context window" };

    private const double _FallbackAllowanceThresholdPercent = 90;

    // Routed through the shared ApplyUsage (AC-761) instead of setting ContextUsedPercent/RateLimits directly, so a
    // merge survives an incomplete snapshot and gets a threshold (AC-660, AC-775).
    private void _RefreshLimits()
    {
        var status = _runtime?.CurrentStatus;

        if (status is { HasAny: true })
        {
            _sharedUsageCache?.Set(_profile?.ProviderConfig, status);
        }
        else
        {
            status = _sharedUsageCache?.TryGet(_profile?.ProviderConfig);
        }

        if (status is not { HasAny: true })
        {
            return;
        }

        var providerId = _profile?.ProviderConfig is PluginProviderConfig plugin ? plugin.ProviderId : null;
        var declared = providerId is not null ? _pluginProviderRegistry?.Resolve(providerId)?.UsageSignals : null;
        UsageProviderId = providerId;

        var signals = new List<PluginUsageSignal>();
        var readings = new List<PluginUsageReading>(status.RateLimits.Count + 1);

        if (status.ContextUsedPercent is { } context)
        {
            var signal = declared?.FirstOrDefault(s => s.Kind == PluginUsageSignalKind.Fill) ?? _FallbackContextSignal;
            signals.Add(signal);
            readings.Add(new PluginUsageReading(signal.Key, context, null));
        }

        foreach (var window in status.RateLimits)
        {
            var signal = declared?.FirstOrDefault(s => s.Label == window.Label)
                ?? new PluginUsageSignal(window.Label, window.Label, PluginUsageSignalKind.Allowance, _FallbackAllowanceThresholdPercent);
            signals.Add(signal);
            readings.Add(new PluginUsageReading(signal.Key, window.UsedPercent, window.ResetsAt));
        }

        ApplyUsage(signals, readings);
    }

    // AC-761 F3: starts (once) the light idle timer that re-reads the driver's already-known status every 30s, so
    // a reply that landed after its turn's publish grace is not stuck there until the next turn completes.
    private void _StartUsageCatchUpTimer()
    {
        if (_usageCatchUpTimer is not null)
        {
            return;
        }

        _usageCatchUpTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _usageCatchUpTimer.Tick += (_, _) => _RefreshLimits();
        _usageCatchUpTimer.Start();
    }

    protected override async ValueTask DisposeCoreAsync()
    {
        // It was left unawaited so the turn could settle without waiting on a file, and a session closing right behind
        // it would otherwise take the process down before the record reached disk (AC-251).
        await _DrainUsageWritesAsync();

        await _StopRuntimeAsync();

        // AC-713: a running flow's subprocess must not outlive the pane that started it.
        _loginPollTimer?.Stop();
        _usageCatchUpTimer?.Stop();
        // AC-786: stopped directly rather than left to its own !IsBusy guard, which never fires once the
        // runtime is torn down mid-turn — the guard belongs to the Tick handler's own rescheduling, not dispose.
        _StopSignOfLifeClock();
        foreach (var entry in Transcript)
        {
            if (entry.LoginFlow is { } loginFlow)
            {
                await loginFlow.DisposeAsync();
            }
        }
    }

    // Ends this panel's runtime and detaches from it, leaving the panel itself intact (AC-564).
    private async Task _StopRuntimeAsync()
    {
        if (_runtime is not null)
        {
            _runtime.EventAppended -= _OnSessionEvent;
        }

        // AC-529: ahead of the null guard, because a teardown that finds the runtime already gone still has the last
        // window's events queued.
        if (_eventQueue.HasWork)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                _eventQueue.Flush();
            }
            else
            {
                Dispatcher.UIThread.Post(_eventQueue.Flush);
            }
        }

        if (_runtime is null)
        {
            return;
        }

        var runtime = _runtime;
        _runtime = null;
        OnPropertyChanged(nameof(IsSessionReady));

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
