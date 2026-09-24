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
    // AC-1376: the backend half of this pane — runtime, turn gate and queue, the send funnel and the clocks. This view
    // model draws what it reports and hands it what the operator does.
    private readonly SessionHost<QueuedMessageViewModel> _host;

    // AC-409: written on a live permission-mode switch (see `OnSelectedPermissionModeChanged`). Null in the design-time/unit-test graph, where the switch simply is not persisted.
    private readonly SessionStateRecorder? _sessionStateRecorder;

    // True only while `ReplayRecordedTranscriptAsync` is repainting rows that came out of the log — recording them
    // straight back would append a second version of every row on every restart.
    private bool _replayingRecordedTranscript;

    // AC-1377: true while a host upsert is being drawn onto its row. The host already holds that version, so what the
    // drawing changes here is not recorded back — that echo would write every streamed delta twice.
    private bool _applyingHostRow;

    // Every row drawn here by id, nested ones included, so an upsert lands on the row it names instead of adding one.
    private readonly Dictionary<string, TranscriptEntryViewModel> _rowsById = new(StringComparer.Ordinal);

    // The reply, fence and table a streamed answer is being split into (AC-1238/1265/1272), rebuilt from the rows' flags.
    private List<TranscriptEntryViewModel>? _replyRows;
    private List<TranscriptEntryViewModel>? _codeSpanRows;
    private List<TranscriptEntryViewModel>? _tableSpanRows;

    // The top-level rows the fold in flight added, in order.
    private readonly List<TranscriptEntryViewModel> _addedByFold = [];

    // Resolves a Plugin-provider profile's own display name for the header's kind chip (AC-537) — the same registry
    // `Converters.ProfileDisplayConverter` uses for the profile picker, injected here rather than reaching into that
    // converter's static seam.
    private readonly IPluginProviderRegistry? _pluginProviderRegistry;

    // AC-713: the generic login gate/starter, dispatched to whichever provider plugin the profile below names.
    private readonly IProfileLoginChecker? _loginChecker;
    private readonly IProfileLoginStarter? _loginStarter;

    // AC-740: null in the design-time/unit-test graph, where the @-mention picker's file source always answers empty.
    private readonly IMentionFileSource? _mentionFileSource;

    // AC-1239: a swallowed launch failure left no trace anywhere. Null in the design-time/unit-test graph.
    private readonly ILogger<SessionViewModel>? _logger;

    // AC-713: the profile this session started under — what an auth error or the poll timer below check against.
    private SessionProfile? _profile;

    // AC-1088: reads a clamped tool result back in full from the provider's own transcript. Null in tests and
    // wherever the host was built without one — the row then says the full result is unavailable, which it is.
    private readonly ISessionTranscriptReader? _transcriptReader;

    // The session id the CLI reports on its events, which is what it names its transcript file.
    private string? _cliSessionId;

    // The session itself — driver, event pump, lifetime — lives in the runtime (#68), which the host creates once the
    // profile is known and the manager owns. This panel is one of its consumers, not its owner.
    private ISessionRuntime? _Runtime => _host.Runtime;

    // The offer this pane was restored with, captured at the top of `StartConfiguredAsync` when it is still set
    // (AC-410).
    private SessionRestorePlan? _restoredOfferSnapshot;

    // The per-session plugin-provider launch options (sandbox, model) from the New-session dialog, set the same way as `SessionPanelViewModel.McpServerSelection` just before `StartWithProfileAsync` reads them.
    private IReadOnlyDictionary<string, string>? _launchOptions;

    // Assistant-text rows added since the last `TurnCompleted` — a turn can produce several (text, tool call, more text), so the read-aloud trigger (#35) reads all of them, not just the last.
    private readonly List<TranscriptEntryViewModel> _currentTurnAssistantEntries = [];

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

    private void _OnHostBusyChanged()
    {
        OnPropertyChanged(nameof(ShowThinkingIndicator));
        OnPropertyChanged(nameof(IsBusy));
        CompactCommand.NotifyCanExecuteChanged();
    }

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

    // AC-1088: one clamped result in full, from the transcript the CLI writes anyway. Null whenever it cannot be
    // had — no reader, no session id yet, or a transcript that has been cleaned up or came from another machine.
    // Called off the UI thread; it reads a file and must stay as cheap to fail as it is to succeed.
    internal string? ReadFullToolResult(string toolUseId) =>
        _transcriptReader?.ReadToolResult(_profile, _cliSessionId, toolUseId);

    // A turn pauses on a question/permission and then keeps streaming into the same growing entry afterwards (AC-97).
    private int _readAloudFlushedLength;

    // AC-597/598: whether anything has actually been spoken this turn. What decides both fillers — a lead-in is
    // only owed when the model gave none, and a sign of life only when the operator has heard nothing since.
    private bool _spokenSomethingThisTurn;

    // Rotated so the same words never come twice in a row. Deliberately not reset per turn: it is the sequence of
    // fillers the operator hears that has to vary, not the sequence within one turn.
    private int _spokenFillerRotation;

    // AC-598: how many times the host's sign-of-life clock has said "still on it" this turn.
    private int _signOfLifeRepeat;

    // Set when an "exit" message is dispatched with auto-close on, so the next completed turn closes the session (T10).
    private bool _closeAfterTurn;

    // The most recently dispatched user turn (text + images), so a failed TurnCompleted row's Retry action
    // (AC-728) can resend exactly what was sent — the operator does not have to retype it.
    private (string Text, IReadOnlyList<Core.Sessions.ImageAttachment> Images)? _lastDispatchedUserTurn;

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
    public virtual bool IsSessionReady => _Runtime is { IsRunning: true };

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

    // Messages typed while a turn was in flight, dispatched in order as turns complete (T8). The host's queue.
    public ObservableCollection<QueuedMessageViewModel> QueuedMessages => _host.Queue;

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
    public bool CombineQueuedMessages
    {
        get => _host.CombineQueued;
        set => _host.CombineQueued = value;
    }

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

    // The host's turn-in-flight flag; its change notifications are raised from `_OnHostBusyChanged`.
    public bool IsBusy
    {
        get => _host.IsBusy;
        set => _host.IsBusy = value;
    }


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

    // True from a `SessionError` until the turn it ended is superseded — by a fresh send (where `IsBusy` outranks
    // it in `_RecomputeStatus`) or by the next `TurnCompleted` success (AC-1309). Drives `SessionStatus.Failed`.
    private bool _lastTurnFailed;

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
                _RegisterRow(entry);
                entry.Session = this;
                entry.ReadingLevel = ReadingLevel;
                entry.PropertyChanged += _OnEntryPropertyChanged;
                _RecordRow(entry);
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

    // AC-1377: the host forms the rows and this draws them. An upsert lands on the row of its id or adds that row — at
    // the top level, or under the anchor whose snapshot carries it (AC-146) — and is never recorded back.
    private void _OnRowUpserted(TranscriptRowUpsert upsert)
    {
        _applyingHostRow = true;
        try
        {
            _DrawRow(upsert.Row, anchor: null);
        }
        finally
        {
            _applyingHostRow = false;
        }
    }

    private void _DrawRow(TranscriptSnapshotEntry entry, TranscriptEntryViewModel? anchor)
    {
        if (_rowsById.TryGetValue(entry.Id, out var row))
        {
            _UpdateRow(row, entry);
        }
        else
        {
            row = _NewRow(entry);
            _rowsById[entry.Id] = row;
            if (anchor is null)
            {
                _addedByFold.Add(row);
                Transcript.Add(row);
            }
            else
            {
                anchor.SubAgentRows.Add(row);
            }
        }

        foreach (var nested in entry.SubAgentRows ?? [])
        {
            _DrawRow(nested, row);
        }
    }

    // A row joins the reply, fence or table it continues when it is made (AC-1238/1265/1272); the row that opened a
    // split fence or table joins its group once it is sealed, in `_UpdateRow`.
    private TranscriptEntryViewModel _NewRow(TranscriptSnapshotEntry entry)
    {
        var kind = Enum.Parse<TranscriptEntryKind>(entry.Kind);
        var row = new TranscriptEntryViewModel(kind, entry.Text, entry.Timestamp)
        {
            Id = entry.Id,
            ToolName = entry.ToolName,
            InputJson = entry.InputJson,
            ToolUseId = entry.ToolUseId,
            IsFailedTurnRow = entry.IsFailedTurnRow,
            IsExpanded = kind == TranscriptEntryKind.Thinking,
        };

        if (entry.StartsReply)
        {
            _replyRows = [];
        }

        if (entry.StartsReply || entry.IsReplyContinuation)
        {
            _replyRows?.Add(row);
            row.ReplyRows = _replyRows;
        }

        if (entry.StartsInsideCodeBlock)
        {
            _codeSpanRows?.Add(row);
            row.CodeSpanRows = _codeSpanRows;
        }

        if (entry.StartsInsideTable)
        {
            _tableSpanRows?.Add(row);
            row.TableSpanRows = _tableSpanRows;
        }

        _UpdateRow(row, entry);
        return row;
    }

    private void _UpdateRow(TranscriptEntryViewModel row, TranscriptSnapshotEntry entry)
    {
        row.Text = entry.Text;
        row.IsResultError = entry.IsResultError;
        row.TruncatedFromChars = entry.TruncatedFromChars;
        if (entry.ResultText is { } result && result != row.ResultText)
        {
            row.ApplyResult(result, entry.IsResultError, entry.TruncatedFromChars, entry.BackgroundTaskId);
        }

        row.PermissionDecision = entry.PermissionDecision;
        row.IsPendingPermission = entry.IsPendingPermission;
        row.ErrorKind = entry.ErrorKind ?? SessionErrorKind.Unknown;
        row.RetryAfter = entry.RetryAfter;
        row.IsReplyContinuation = entry.IsReplyContinuation;
        row.IsReplyTail = !entry.ReplyContinuesBelow;
        row.StartsInsideCodeBlock = entry.StartsInsideCodeBlock;
        row.EndsInsideCodeBlock = entry.EndsInsideCodeBlock;
        row.StartsInsideTable = entry.StartsInsideTable;
        row.EndsInsideTable = entry.EndsInsideTable;
        row.TableSpanRevision = entry.TableSpanRevision;

        if (entry.EndsInsideCodeBlock && row.CodeSpanRows is null)
        {
            _codeSpanRows = [row];
            row.CodeSpanRows = _codeSpanRows;
        }

        if (entry.EndsInsideTable && row.TableSpanRows is null)
        {
            _tableSpanRows = [row];
            row.TableSpanRows = _tableSpanRows;
        }
    }

    // A row drawn here, nested ones included, so the host's upsert for it lands on it rather than beside it.
    private void _RegisterRow(TranscriptEntryViewModel entry)
    {
        _rowsById.TryAdd(entry.Id, entry);
        foreach (var nested in entry.SubAgentRowsForDisplay)
        {
            _RegisterRow(nested);
        }
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
        if (e.PropertyName is { } name && RecordedRowProperties.Contains(name) && sender is TranscriptEntryViewModel changed)
        {
            _RecordRow(changed);
        }

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

    // AC-1090: the row properties the transcript log carries. Named rather than "any change", because a
    // reading-level switch touches every row on screen and would write the whole transcript out again. The two
    // pending flags are here for what they imply: they flip when a question card's answers (AC-955) change.
    private static readonly HashSet<string> RecordedRowProperties =
    [
        nameof(TranscriptEntryViewModel.Text),
        nameof(TranscriptEntryViewModel.ResultText),
        nameof(TranscriptEntryViewModel.IsResultError),
        nameof(TranscriptEntryViewModel.PermissionDecision),
        nameof(TranscriptEntryViewModel.IsPendingPermission),
        nameof(TranscriptEntryViewModel.IsPendingBrokerAnswer),
        nameof(TranscriptEntryViewModel.QuestionPrompts),
        nameof(TranscriptEntryViewModel.LatestReply),
        nameof(TranscriptEntryViewModel.ErrorKind),
        nameof(TranscriptEntryViewModel.RetryAfter),
        nameof(TranscriptEntryViewModel.SubAgentRowsForDisplay),
    ];

    // AC-1377: a row this pane formed or changed itself goes to the host, which holds the transcript and writes it.
    private void _RecordRow(TranscriptEntryViewModel entry)
    {
        if (!_replayingRecordedTranscript && !_applyingHostRow)
        {
            _host.RecordRow(TranscriptSnapshot.Capture(entry));
        }
    }

    // AC-1080: replay or roll, decided by what this launch is actually resuming. One rule rather than a copy per
    // caller — the assistant host and the restore path both start a pane that may or may not continue its log.
    public Task PrepareRecordedTranscriptAsync(SessionResume resume, CancellationToken cancellationToken = default) =>
        resume.Mode == SessionResumeMode.BySessionId
            ? ReplayRecordedTranscriptAsync(cancellationToken)
            : ArchiveRecordedTranscriptAsync(cancellationToken);

    // AC-1090: repaints this pane with the conversation Cockpit itself recorded, before anything new is added.
    // Only what the log holds — whether the provider is also resuming, and what the operator is told about the
    // difference, is the restore path's call, not this one's.
    public async Task ReplayRecordedTranscriptAsync(CancellationToken cancellationToken = default)
    {
        if (!_host.RecordsTranscript)
        {
            return;
        }

        var recorded = await _host.LoadRecordedTranscriptAsync(cancellationToken).ConfigureAwait(true);
        if (recorded is null)
        {
            // AC-1080: the session still starts, so without this row the operator faces an empty window that looks
            // like a pane with no history rather than one whose history could not be read back.
            Transcript.Add(new TranscriptEntryViewModel(
                TranscriptEntryKind.Error,
                "This session's earlier conversation could not be read back. It continues from here; what came before is not shown."));
            return;
        }

        _replayingRecordedTranscript = true;
        try
        {
            foreach (var entry in TranscriptSnapshot.Restore(recorded))
            {
                Transcript.Add(entry);
            }
        }
        finally
        {
            _replayingRecordedTranscript = false;
        }
    }

    // AC-1090: rolls this pane's recorded conversation aside, for a launch that starts a new one rather than
    // continuing this one — a new conversation is a new log.
    public Task ArchiveRecordedTranscriptAsync(CancellationToken cancellationToken = default) =>
        _host.ArchiveRecordedTranscriptAsync(cancellationToken);

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
        _host = _BuildHost(sessionManager: null, turnInboxDelivery: null, loginChecker: null, sharedUsageCache: null, logger: null, transcriptStore: null);
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
        ISessionTranscriptStore? transcriptStore = null,
        IPluginProviderRegistry? pluginProviderRegistry = null,
        VoiceOverlayCoordinator? voiceOverlay = null,
        IProfileLoginChecker? loginChecker = null,
        IProfileLoginStarter? loginStarter = null,
        IMentionFileSource? mentionFileSource = null,
        ISharedUsageCache? sharedUsageCache = null,
        ISessionTranscriptReader? transcriptReader = null,
        ILogger<SessionViewModel>? logger = null)
        : base(usageHistory)
    {
        _transcriptReader = transcriptReader;
        // AC-1090: every pane but the design-time/unit-test graph gets the store from the container, which
        // `APaneTakenFromTheContainer_RecordsItsRowsToDisk` holds this to; the host writes it (AC-1377).
        _host = _BuildHost(sessionManager, turnInboxDelivery, loginChecker, sharedUsageCache, logger, transcriptStore);
        _eventQueue = new SessionEventQueue(Apply);
        _turnInboxDelivery = turnInboxDelivery;
        _sessionStateRecorder = sessionStateRecorder;
        _pluginProviderRegistry = pluginProviderRegistry;
        _loginChecker = loginChecker;
        _loginStarter = loginStarter;
        _mentionFileSource = mentionFileSource;
        _logger = logger;
        MentionPicker = new MentionPickerViewModel(_MentionPathsAsync, () => WorkingDirectory);
        _TrackPendingAttachments();
        InitializeVoice(voicePushToTalk, voiceSettingsStore, voicePlaybackQueue, openMicState, voiceOverlay);
        CloseRequested += (_, _) => _host.StopPolling();
    }

    // The host reports on the consumer's thread except for its clocks, which tick on the pool and are posted here.
    private SessionHost<QueuedMessageViewModel> _BuildHost(
        ISessionManager? sessionManager,
        IAgentTurnInboxDelivery? turnInboxDelivery,
        IProfileLoginChecker? loginChecker,
        ISharedUsageCache? sharedUsageCache,
        ILogger? logger,
        ISessionTranscriptStore? transcriptStore)
    {
        var host = new SessionHost<QueuedMessageViewModel>(
            () => PaneId, sessionManager, TimeProvider.System, turnInboxDelivery, loginChecker, sharedUsageCache, logger, transcriptStore);
        host.EventAppended += hostEvent => _eventQueue.Enqueue(hostEvent.Event);
        host.RowUpserted += _OnRowUpserted;
        host.BusyChanged += _OnHostBusyChanged;
        host.TurnStarting += _OnTurnStarting;
        host.TurnFailedToStart += _OnTurnFailedToStart;
        host.MailDelivered += _NoteDeliveredMail;
        host.LoginChecked += loggedIn => _OnUiThread(() => _WhilePolling(() => ReportLoginStatus(loggedIn)));
        host.UsageCatchUpDue += () => _OnUiThread(() => _WhilePolling(_RefreshLimits));
        host.SignOfLifeDue += arm => _OnUiThread(() => _OnSignOfLife(arm));
        return host;
    }

    // A DispatcherTimer stopped on close ticked no more; a pool tick posted just before the close still lands here.
    private void _WhilePolling(Action action)
    {
        if (_host.IsPolling)
        {
            action();
        }
    }

    // A throw in `action` reaches the UI thread's own net (Program.cs), as a DispatcherTimer tick's did.
    private static void _OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    // This is the pane kind turn-start delivery works on (AC-394): the host composes its turns as typed calls on a
    // runtime, so there is a real moment before one goes out to put a peer's message in.
    public override bool DeliversInboxAtTurnStart => _turnInboxDelivery is not null;

    // Use `IsSessionReady` because a driver that never started can leave a runtime accepting sends into nothing.
    // AC-1321: a held pane is not a candidate for a wake either, so the gateway refuses it instead of the funnel.
    public override bool CanTakeAPrompt => IsSessionReady && TurnsHeldBecause is null;

    // AC-1321: why no new turn may start on this pane, else null — set by the assistant host while a paired
    // controller holds the line. The hold itself, and sending what waited behind it, is the session host's.
    public string? TurnsHeldBecause
    {
        get => _host.TurnsHeldBecause;
        set
        {
            if (_host.TurnsHeldBecause == value)
            {
                return;
            }

            _host.TurnsHeldBecause = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanTakeAPrompt));
        }
    }

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
        if (_Runtime is not null)
        {
            return;
        }

        // AC-410: still set here for a restored pane's first launch — see _restoredOfferSnapshot's own doc for why
        // it has to be captured now rather than read again once the first turn actually completes.
        _restoredOfferSnapshot = RestoreOffer;

        // AC-713: what an auth-related error or the login-poll timer below check against.
        _profile = profile;
        if (profile is not null)
        {
            _host.StartLoginPoll(profile);
        }

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
        _host.PreApprove(preApprovedTools, preApproveAllTools);

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
        if (_Runtime is not { IsRunning: true })
        {
            // AC-1239: the quieter half — StartAsync returned without throwing and nothing is running, which used to
            // leave Status reading "Session started." on a session that never did. A launch that threw already spoke.
            // Only with a runtime in hand: a null one means no launch was attempted (the design-time graph) or that a
            // teardown took it mid-start, and neither of those failed to start.
            if (StartFailure is null && _Runtime is not null)
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
        if (_Runtime is null)
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
            [.. _host.PreApprovedTools],
            _host.PreApprovesAllTools);
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

        _host.ResetTranscriptStreaming();
        _currentTurnAssistantEntries.Clear();
        _readAloudFlushedLength = 0;
        _spokenSomethingThisTurn = false;
        _StopSignOfLifeClock();
        ClearCurrentTurnImages();
        _hasCompletedATurn = false;
        _needsAttention = false;
        _lastTurnFailed = false;
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
        if (!_host.CanLaunch)
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
            var runtime = _host.Attach(profile);

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
            _host.StartUsageCatchUp();

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
        }
        finally
        {
            // In a finally because failure reporting can throw too. Only the starting banner is cleared here —
            // IsSessionReady is settled by the single caller (StartConfiguredAsync) on both paths.
            IsStarting = false;
        }
    }

    // Live-toggles auto-approval of tool calls on the running session's driver (local sessions).
    partial void OnAutoApproveToolsChanged(bool value)
    {
        _ = _Runtime?.SetAutoApproveToolsAsync(value);
    }

    // Live-switches the running session's permission mode. No-op before the session has started.
    partial void OnSelectedPermissionModeChanged(PermissionModeOption value)
    {
        if (_Runtime is not { IsRunning: true })
        {
            return;
        }

        _ = _SetPermissionModeSafeAsync(value.Value);
    }

    // Live-switches the running session's model. No-op before the session has started.
    partial void OnSelectedModelChanged(ModelOption value)
    {
        if (_Runtime is not { IsRunning: true })
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
        if (_Runtime is not { IsRunning: true })
        {
            return;
        }

        _ = _SetMaxThinkingTokensSafeAsync(value.MaxThinkingTokens);
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (_Runtime is not { IsRunning: true })
        {
            return;
        }

        try
        {
            await _Runtime.InterruptAsync();
            Status = "Interrupted.";
            _host.InterruptRequested = true;

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
        if (_Runtime is not { IsRunning: true } || TurnsHeldBecause is not null || !Capabilities.SupportsContextCompaction)
        {
            return false;
        }

        IsBusy = true;
        _RecomputeStatus();

        try
        {
            await _Runtime.CompactContextAsync();
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
        if (_Runtime is null)
        {
            return;
        }

        try
        {
            await _Runtime.SetPermissionModeAsync(mode);

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
        if (_Runtime is null)
        {
            return;
        }

        try
        {
            await _Runtime.SetModelAsync(model);
        }
        catch (Exception ex)
        {
            Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, $"Model switch failed: {ex.Message}"));
        }
    }

    private async Task _SetMaxThinkingTokensSafeAsync(int maxThinkingTokens)
    {
        if (_Runtime is null)
        {
            return;
        }

        try
        {
            await _Runtime.SetMaxThinkingTokensAsync(maxThinkingTokens);
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
        if (_Runtime is null)
        {
            return;
        }

        foreach (var option in _Runtime.LiveOptions)
        {
            LiveControls.Add(new LiveControlViewModel(option, _SetLiveOptionSafeAsync));
        }
    }

    // Live-switches one of the provider's generic controls on the running session's driver (#45 D4).
    private async Task _SetLiveOptionSafeAsync(string key, string value)
    {
        if (_Runtime is null)
        {
            return;
        }

        try
        {
            await _Runtime.SetLiveOptionAsync(key, value);
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
        if (_Runtime is not { IsRunning: true } || !CanPasteImages)
        {
            return false;
        }

        IReadOnlyList<Core.Sessions.ImageAttachment> images = [Core.Sessions.ImageAttachment.FromBytes(screenshotPng, "image/png")];

        await _host.SubmitAsync(new QueuedMessageViewModel(caption, images, replyTo: null, message => QueuedMessages.Remove(message)));
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
        if (_Runtime is not { IsRunning: true })
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
        await _host.SubmitAsync(
            new QueuedMessageViewModel(text, images, replyTo, m => QueuedMessages.Remove(m)),
            Capabilities.SupportsMidTurnInput);
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

    // What a turn the host is about to send looks like here: echoed into the transcript, the turn marked busy.
    // `replyTo` (AC-935) rides only as far as the row's own reference — `_lastDispatchedUserTurn` and the "exit" check
    // below stay on the bare text, so a reply prefix never changes retry or auto-close behaviour.
    private void _OnTurnStarting(QueuedPrompt prompt)
    {
        var text = prompt.Text;
        var images = prompt.Images;
        var replyTo = (prompt as QueuedMessageViewModel)?.ReplyTo;

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
        _host.InterruptRequested = false;
        IsBusy = true;
        _needsAttention = false;
        _RecomputeStatus();

        // Cleared when the turn completes, or in `_OnTurnFailedToStart` if the send never happened (AC-116).
        _RememberTurnImages(images);
    }

    private void _OnTurnFailedToStart(QueuedPrompt prompt, Exception exception)
    {
        ClearCurrentTurnImages();
        Transcript.Add(new TranscriptEntryViewModel(
            TranscriptEntryKind.Error, SendFailureMessage(exception, _Runtime is { IsRunning: true })));
        IsBusy = false;
        _RecomputeStatus();
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
        // AC-1324: a node's question drawn here — the same row, but the click goes over the line, not to a runtime.
        if (entry.NodePermission is { } onNode)
        {
            await _AnswerOnNodeAsync(entry, onNode, allow);
            return;
        }

        if (_Runtime is null || entry.ToolUseId is null)
        {
            return;
        }

        _MarkDecided(entry, answersJson is not null ? "Answered" : allow ? "Allowed" : "Denied");
        await _Runtime.RespondToPermissionAsync(entry.ToolUseId, allow, answersJson, CancellationToken.None);
    }

    // AC-1324: three outcomes, each told on the row itself. Answered there: closed with the machine named. No
    // longer open there (answered on the node, or the session gone): closed, nothing done. Line failed: the row
    // reopens with the reason, so the buttons work again once the node is back.
    private static async Task _AnswerOnNodeAsync(TranscriptEntryViewModel entry, NodePermissionOrigin onNode, bool allow)
    {
        // Closed before the line is called, like the local path (`_MarkDecided` before the runtime): the call takes
        // up to the client's budget, and a second click in that window would be a second answer over the line.
        if (!entry.IsPendingPermission)
        {
            return;
        }

        entry.IsPendingPermission = false;
        entry.PermissionDecision = $"Answering on {onNode.Node}…";

        var reply = await onNode.Answer(allow);
        if (reply.Error is { } error)
        {
            entry.PermissionDecision = $"Not answered — {error}";
            entry.IsPendingPermission = true;
            return;
        }

        entry.PermissionDecision = reply.Answered
            ? $"{(allow ? "Allowed" : "Denied")} on {onNode.Node}"
            : $"Already answered on {onNode.Node}";
    }

    // AC-1324: the controller's click, arriving by tool-use id rather than by row. False when no such row is
    // open here — answered already, or never asked — and then nothing is done.
    internal async Task<bool> RespondToPermissionByIdAsync(string toolUseId, bool allow)
    {
        if (PendingToolPermissionRows().FirstOrDefault(row => string.Equals(row.ToolUseId, toolUseId, StringComparison.Ordinal)) is not { } entry)
        {
            return false;
        }

        await RespondToPermissionAsync(entry, allow);
        return true;
    }

    // AC-1324: every consent row waiting on a click, top-level and nested alike — the rows a controller draws for
    // this session. A question row (AskUserQuestion) wants an answer, not a click, so it is not among them.
    internal IEnumerable<TranscriptEntryViewModel> PendingToolPermissionRows() => _PendingRows().Where(row => !row.HasQuestionPrompts);

    private IEnumerable<TranscriptEntryViewModel> _PendingRows() =>
        Transcript.Concat(Transcript.SelectMany(row => row.SubAgentRowsForDisplay)).Where(row => row.IsPendingPermission);

    private void _MarkDecided(TranscriptEntryViewModel entry, string decision)
    {
        entry.PermissionDecision = decision;
        entry.IsPendingPermission = false;
        // AC-532: the operator's decision may be what the composer's activity band was showing "waiting for
        // permission" for — re-raise so it reverts to the normal running text (or goes quiet, if this was the
        // call's only reason to still be shown).
        _RaiseActiveToolActivityChanged();

        // AC-1324: `needsYou` means "stopped on a question nobody answered", and this was the last one — the
        // session runs on from here, so it stops flagging itself the moment the click lands, not at the next message.
        if (_needsAttention && !_PendingRows().Any())
        {
            _needsAttention = false;
            _RecomputeStatus();
        }
    }

    private async Task AllowAlwaysAsync(TranscriptEntryViewModel entry, PermissionRuleScope scope)
    {
        if (_Runtime is null || entry.ToolUseId is null || entry.ToolName is null || entry.NodePermission is not null)
        {
            return;
        }

        _MarkDecided(entry, scope == PermissionRuleScope.Wildcard
            ? $"Always allowed ({entry.ToolName}:*)"
            : $"Always allowed (exact: {entry.ToolName})");

        await _Runtime.AllowPermissionAlwaysAsync(entry.ToolUseId, entry.ToolName, entry.InputJson ?? "{}", scope);
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
        // AC-1238: a reply split at its own blank lines already carries them, so a continuation is concatenated
        // rather than separated — inserting a separator there would shift every index past it by two characters
        // and re-speak or skip that much. Rows separated by a tool call still get theirs.
        var prose = string.Concat(_currentTurnAssistantEntries.Select(
            (entry, index) => index == 0 || entry.IsReplyContinuation ? entry.Text : "\n\n" + entry.Text));
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

        _host.RestartSignOfLife(AssistantSpokenFillers.SignOfLifeDelay(_signOfLifeRepeat));
    }

    // One tick of the host's clock, on the UI thread; an arm a restart or stop has since replaced says nothing.
    private void _OnSignOfLife(int arm)
    {
        if (!_host.IsCurrentSignOfLife(arm))
        {
            return;
        }

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

        _host.RestartSignOfLife(AssistantSpokenFillers.SignOfLifeDelay(_signOfLifeRepeat));
    }

    private void _StopSignOfLifeClock()
    {
        _host.StopSignOfLife();
        _signOfLifeRepeat = 0;
    }

    // The host re-issues the runtime's events off the UI thread (#68); marshalling them onto it is this panel's job,
    // because it is the consumer that touches UI — a headless consumer of the same host marshals nothing (AC-529).
    private readonly SessionEventQueue _eventQueue;

    // Raised when the session makes real tool progress — a tool call surfacing or a tool result landing (AC-215/stall).
    // An embedder that fails a silent step on a stall deadline (Autopilot) resets that deadline on this, so a step that
    // is slow because it is working hard is not mistaken for a stuck one. Not raised on text/thinking on purpose.
    public event Action? ToolActivity;

    // internal (rather than private) so `Cockpit.Core.Tests` can drive it directly, bypassing `Dispatcher.UIThread` — see `_eventQueue`.
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

        // AC-1088: the CLI names its own transcript after this id, so it is what finds a clamped result back.
        // Taken off whichever event reports it rather than off `SessionInitialized` alone — a resumed session
        // carries it from its first event, and a `/clear` gives the conversation a new one mid-session.
        _cliSessionId = evt.SessionId ?? _cliSessionId;

        // AC-1319: a CLI that keeps its own input queue (SupportsMidTurnInput) starts turns this pane never sent — a
        // prompt queued past the `result`, or a task-notification for a finished background task — with no turn-start
        // line, so the agent's own first output is the signal. A sub-agent's output does not speak for the main agent.
        if (!IsBusy && Capabilities.SupportsMidTurnInput && evt.ParentToolUseId is null
            && evt is AssistantTextDelta or AssistantThinkingDelta or AssistantTextCompleted or ToolUseRequested or ToolResult)
        {
            IsBusy = true;
            _RecomputeStatus();
        }

        // AC-1377: the host forms the rows, drawn here as its upserts arrive; what follows is what this pane does
        // with the row the event landed on, decided from that one snapshot.
        _addedByFold.Clear();
        var fold = _host.ApplyToTranscript(evt);
        var landed = fold.Row is { } landedRow ? _rowsById[landedRow.Id] : null;

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
                // AC-146: a sub-agent's text, in its lane or orphaned, is never the reply that is read aloud.
                if (string.IsNullOrEmpty(delta.ParentToolUseId))
                {
                    _currentTurnAssistantEntries.AddRange(_addedByFold);
                }

                break;

            case AssistantTextCompleted completed:
                // A sub-agent's own narration is not the session's answer to the operator, so it never reaches the
                // read-aloud queue or the output-text signal (AC-146).
                if (!string.IsNullOrEmpty(completed.ParentToolUseId))
                {
                    break;
                }

                _currentTurnAssistantEntries.AddRange(_addedByFold);
                RaiseOutputText(completed.Text);
                break;

            case ToolUseRequested toolUse:
                // AC-146: a sub-agent's own tool call nests under its Task row and is not what the turn waits on.
                if (fold.InSubAgentLane || landed is not { } toolUseRow)
                {
                    break;
                }

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
                // The row the result coupled to (L14), or null when it fell back to a row of its own.
                var toolUseEntry = landed is { Kind: TranscriptEntryKind.ToolUse } coupled ? coupled : null;
                if (toolUseEntry is not null)
                {
                    _TrackBackgroundToolRow(toolUseEntry);
                }

                // AC-146: a sub-agent's own result never raises the signals a top-level one does.
                if (fold.InSubAgentLane)
                {
                    break;
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
                // AC-215: the host allowed a pre-authorized tool of a self-driving run itself, so nobody is asked.
                if (fold.Row is not { IsPendingPermission: true } || landed is not { } entry)
                {
                    break;
                }

                // AC-715: an AskUserQuestion rides this same callback but asks for an answer, not consent —
                // parse its questions here so the row renders them as choices instead of Allow/Deny over raw
                // JSON. Any other tool parses to nothing and keeps the ordinary consent card.
                entry.QuestionPrompts = permission.ToolName == AskUserQuestionToolName
                    ? AskUserQuestionViewModel.Parse(permission.InputJson)
                    : null;
                // AC-532: a top-level call stalling on this prompt is why the turn looks idle right now —
                // flip the composer's activity band from "running" to "waiting for permission" so that reads
                // as waiting on the operator rather than as the tool quietly still working.
                _RaiseActiveToolActivityChanged();

                // AC-996: the host gave a permission without a tool-use row one of its own, so there is always
                // something to click while the session parks on needs-attention.
                _needsAttention = true;
                // Speak the lead-in the reply gave before this tool needs approval, rather than holding it back
                // until the operator answers the prompt (AC-97).
                _FlushPendingProseForReadAloud();
                _RecomputeStatus();
                break;

            case Question:
                // Same as a permission prompt: a question pauses the turn, so speak what was said before it now.
                _FlushPendingProseForReadAloud();
                break;

            case TurnCompleted turn:
                // AC-1031: consumed once, right here — a turn's own IsError below must not keep reading this
                // as "interrupted" once we've reported it, or a later genuine failure would render as one too.
                var wasInterrupted = _host.InterruptRequested;
                _host.InterruptRequested = false;

                if (fold.Row is { IsFailedTurnRow: true } && landed is { } failedTurnRow)
                {
                    // AC-939: an auth classification means Retry would just fail again — offer the same
                    // login-gate the SessionError branch below uses instead.
                    if (failedTurnRow.ErrorKind == SessionErrorKind.AuthRequired && _profile is not null && _loginChecker?.IsLoggedIn(_profile) == false)
                    {
                        failedTurnRow.ActionLabel = "Login";
                        failedTurnRow.ActionCommand = new RelayCommand(() => _StartLoginFlow(failedTurnRow));
                    }
                    // AC-728: same ActionLabel/ActionCommand convention as AC-713's "Login" row. Left unset when
                    // this turn never went through the host's dispatch — a scheduled resume's own first turn
                    // (SendPromptAsync, AC-410) is the one case that applies to.
                    else if (_lastDispatchedUserTurn is { } lastTurn)
                    {
                        failedTurnRow.ActionLabel = "Retry";
                        failedTurnRow.ActionCommand = new RelayCommand(
                            () => _host.DispatchInBackground(new QueuedPrompt(lastTurn.Text, lastTurn.Images)));
                    }
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
                // This success supersedes whatever the previous turn ended in (AC-1309): a Failed session that
                // recovers reads as Done, not stuck on the error that no longer describes it.
                _lastTurnFailed = false;
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

                // A completed turn (success or error result) frees the session, so the host sends the next queued
                // message (T8). A SessionError event does not drain the queue — the chips stay so a
                // broken session isn't cascaded through every queued message.
                _host.CompleteTurn();
                break;

            case SessionError:
                // AC-713: re-checks the profile's own login gate rather than pattern-matching the message.
                if (landed is { } errorEntry && _profile is not null && _loginChecker?.IsLoggedIn(_profile) == false)
                {
                    errorEntry.ActionLabel = "Login";
                    errorEntry.ActionCommand = new RelayCommand(() => _StartLoginFlow(errorEntry));
                }

                // A session error ends the turn without a TurnCompleted, so drop this turn's images here too —
                // otherwise a later image-less turn's tool call could attach the errored turn's stale images (AC-116).
                ClearCurrentTurnImages();
                IsBusy = false;
                _lastTurnFailed = true;
                // Whatever was outstanding died with the session (AC-276). Unlike the TTY route this one has no
                // safety timeout to fall back on, so a sub-agent left in the list here would hold a crashed session
                // on WorkingBackground forever — and make closing it ask "still working?" on the way out.
                _backgroundTasks = [];
                OnPropertyChanged(nameof(HasOutstandingBackgroundShells));
                OnPropertyChanged(nameof(SessionStatusLabel));
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
                OnPropertyChanged(nameof(SessionStatusLabel));
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

            case RateLimitInfo:
            case UnknownEvent:
                break;
        }
    }

    // Derives `SessionStatus` from the flags this view model already tracks: busy while a turn is in flight; see
    // AC-276. `_lastTurnFailed` only decides anything once neither of those outrank it (AC-1309) — a fresh send
    // reads as Busy regardless, which is what lets a retried turn escape Failed without a separate reset.
    private void _RecomputeStatus()
    {
        SessionStatus = (_needsAttention, IsBusy, _HasOutstandingSubAgents, _lastTurnFailed) switch
        {
            (true, _, _, _) => SessionStatus.NeedsAttention,
            (false, true, _, _) => SessionStatus.Busy,
            (false, false, true, _) => SessionStatus.WorkingBackground,
            (false, false, false, true) => SessionStatus.Failed,
            (false, false, false, false) => _hasCompletedATurn ? SessionStatus.Done : SessionStatus.Idle,
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
    public override bool HasOutstandingBackgroundShells =>
        _backgroundTasks.Any(task => task.Kind == BackgroundTaskKind.Shell) || base.HasOutstandingBackgroundShells;

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
        if (_Runtime is not { IsRunning: true } || !CanTakeAPrompt)
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
            // Through the host's one funnel, as the composer's own sends are: a scheduled resume is a real turn on a real
            // session, so mail waiting for this pane belongs on it just as much.
            await _host.SendPromptAsync(prompt);
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

    // AC-761: fallback signal for a profile with no registered plugin provider (an unmigrated legacy Claude
    // profile) — the same numbers ClaudeUsageSignals declares, so it still gets a threshold instead of none.
    private static readonly PluginUsageSignal _FallbackContextSignal =
        new("context", "ctx", PluginUsageSignalKind.Fill, DefaultThresholdPercent: 50) { Description = "Context window" };

    private const double _FallbackAllowanceThresholdPercent = 90;

    // Routed through the shared ApplyUsage (AC-761) instead of setting ContextUsedPercent/RateLimits directly, so a
    // merge survives an incomplete snapshot and gets a threshold (AC-660, AC-775).
    private void _RefreshLimits()
    {
        var status = _host.ReadUsageStatus(_profile?.ProviderConfig);
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

    protected override async ValueTask DisposeCoreAsync()
    {
        // It was left unawaited so the turn could settle without waiting on a file, and a session closing right behind
        // it would otherwise take the process down before the record reached disk (AC-251).
        await _DrainUsageWritesAsync();

        await _StopRuntimeAsync();

        // AC-713, AC-786: the host's clocks stop here — the sign of life's own !IsBusy guard never fires once the
        // runtime is torn down mid-turn — and a running flow's subprocess must not outlive the pane that started it.
        await _host.DisposeAsync();
        _signOfLifeRepeat = 0;
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
        _host.StopListening();

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

        if (_Runtime is null)
        {
            return;
        }

        // The host clears its runtime before its first await, so readiness reads false by the time this is raised.
        var stopping = _host.StopAsync();
        OnPropertyChanged(nameof(IsSessionReady));
        await stopping;
    }
}
