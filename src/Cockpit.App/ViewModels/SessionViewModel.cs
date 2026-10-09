using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Composition;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Events;
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
using Cockpit.Plugins.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.ViewModels;

// F-C1 cockpit: a single Claude Code session rendered as a streaming transcript with a chat-style input box and
// read-only-so-far allow/deny affordances for tool use.
public partial class SessionViewModel : SessionPanelViewModel, ITransientService
{
    // AC-1376/1449: the backend half of this pane — runtime, turn gate and queue, the send funnel and the clocks. This
    // view model draws what it reports and hands it what the operator does.
    private readonly ISessionControl _control;

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
    // converter's static seam. AC-1449: the name and the usage signals, through contracts of their own.
    private readonly ISessionProviderNames? _providerNames;
    private readonly IProviderUsageSignals? _usageSignals;

    // AC-713: the generic login gate/starter, dispatched to whichever provider plugin the profile below names.
    private readonly IProfileLoginChecker? _loginChecker;
    private readonly ISessionLoginFlows? _loginFlows;

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

    // The offer this pane was restored with, captured at the top of `StartConfiguredAsync` when it is still set
    // (AC-410).
    private SessionRestorePlan? _restoredOfferSnapshot;

    // The per-session plugin-provider launch options (sandbox, model) from the New-session dialog, set the same way as `SessionPanelViewModel.McpServerSelection` just before `StartWithProfileAsync` reads them.
    private IReadOnlyDictionary<string, string>? _launchOptions;

    // Assistant-text rows added since the last turn ended — a turn can produce several (text, tool call, more text), so the read-aloud trigger (#35) reads all of them, not just the last.
    private readonly List<TranscriptEntryViewModel> _currentTurnAssistantEntries = [];

    // One top-level tool call the turn is currently waiting on (AC-532).
    private readonly record struct ActiveToolCall(string ToolUseId, string Label, DateTimeOffset StartedAt);

    // Provider-neutral by construction: driven only by a tool call surfacing and its result landing, which every provider
    // that reports tool calls at all reports (AC-532).
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

        // A TaskId no longer reported has finished (or the whole set was wiped, e.g. by a session error): forget its
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

    // The most recently dispatched user turn (text + images), so a failed turn row's Retry action
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
    public virtual bool IsSessionReady => _control.IsRunning;

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

    // Messages typed while a turn was in flight, dispatched in order as turns complete (T8): the host's queue as chips.
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
    public bool CombineQueuedMessages
    {
        get => _control.CombineQueued;
        set => _control.CombineQueued = value;
    }

    // True when there is text or an image to act on, so Send is enabled exactly when it will do
    // something. It does not gate on `IsBusy`: while a turn runs, Send queues the message
    // (T8) rather than being disabled, so you can keep typing ahead without losing input.
    public bool CanSend => (!string.IsNullOrWhiteSpace(InputText) || PendingAttachments.Count > 0) && IsLinkUp;

    // It gates only the *view* (the input box is disabled), deliberately not `CanSend`: the host still submits the
    // run's opening brief through the send path programmatically, which must work even while the composer is off
    // (AC-174).
    [ObservableProperty]
    private bool _isInputEnabled = true;

    // AC-1456: what the composer says while it is empty; a remote pane says why it cannot send yet.
    [ObservableProperty]
    private string _composerPlaceholder = "Send a message...  (Enter = send, Shift+Enter = new line, Ctrl+V = paste text or image)";

    // AC-1469: false while the line to this pane's server is down, so Send and the permission buttons are off with it.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyPropertyChangedFor(nameof(CanAllowForSession))]
    private bool _isLinkUp = true;

    // AC-1469: whether the server's key may answer permission prompts. The server decides again on every answer.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAllowForSession))]
    private bool _mayAnswerPermissions = true;

    // AC-1476: whether the server takes a session-wide allow; an older one would read it as a single allow.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAllowForSession))]
    private bool _allowsForSession;

    public bool CanAllowForSession => IsRemote && MayAnswerPermissions && AllowsForSession && IsLinkUp;

    public string AllowLabel => IsRemote ? "Allow once" : "Allow";

    // AC-1456: undoes FollowRemote's subscriptions when the pane goes, since the handle outlives it.
    private Action? _unfollowRemote;

    // AC-1469: the remote handle's control; null on a local pane.
    private ISessionControl? _remoteControl;
    private ISessionHandle? _remoteHandle;
    private readonly HashSet<TranscriptEntryViewModel> _answering = [];

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
        get => _control.IsBusy;
        set => _control.IsBusy = value;
    }


    // Shows the "Allow all tools" toggle: a local tool session (has tools, but not Claude's own permission modes) whose every MCP call would otherwise need an Allow click.
    [ObservableProperty]
    private bool _showToolAutoApprove;

    // When on, this session runs tool calls without prompting (still shown as tool rows). Applied live to the driver.
    [ObservableProperty]
    private bool _autoApproveTools;

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
    private void _DrawHostRow(TranscriptSnapshotEntry entry)
    {
        _applyingHostRow = true;
        try
        {
            // AC-1518: a nested row comes on its own and names its anchor; it lands there and touches nothing else.
            if (entry.ParentRowId is not { } parentId)
            {
                _DrawRow(entry, anchor: null);
            }
            else if (_rowsById.TryGetValue(parentId, out var anchor))
            {
                _DrawRow(entry, anchor);
            }
        }
        finally
        {
            _applyingHostRow = false;
        }
    }

    // AC-1438: the host's rows after the event log said some of their upserts were missed. A row this pane holds is
    // brought up to date; one it never drew goes in after the row the host has before it, so the order is the host's.
    private void _Resync(IReadOnlyList<TranscriptSnapshotEntry> rows)
    {
        _applyingHostRow = true;
        try
        {
            var previous = -1;
            foreach (var entry in rows)
            {
                var known = _rowsById.TryGetValue(entry.Id, out var row) ? _IndexOfLast(row) : -1;
                previous = known >= 0 ? known : previous + 1;
                _DrawRow(entry, anchor: null, insertAt: known >= 0 ? null : previous);
            }
        }
        finally
        {
            _applyingHostRow = false;
        }
    }

    private void _DrawRow(TranscriptSnapshotEntry entry, TranscriptEntryViewModel? anchor, int? insertAt = null)
    {
        if (_rowsById.TryGetValue(entry.Id, out var row))
        {
            _UpdateRow(row, entry);
        }
        else
        {
            row = _NewRow(entry);
            _rowsById[entry.Id] = row;
            if (anchor is not null)
            {
                anchor.SubAgentRows.Add(row);
            }
            else
            {
                _addedByFold.Add(row);
                Transcript.Insert(insertAt ?? Transcript.Count, row);
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

        // A row being answered on the server keeps its own state until the answer returns (AC-1469).
        if (!_answering.Contains(row))
        {
            row.PermissionDecision = entry.PermissionDecision;
            row.IsPendingPermission = entry.IsPendingPermission;
        }

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
            _control.RecordRow(TranscriptSnapshot.Capture(entry));
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
        if (!_control.RecordsTranscript)
        {
            return;
        }

        var recorded = await _control.LoadRecordedTranscriptAsync(cancellationToken).ConfigureAwait(true);
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
            var restored = TranscriptSnapshot.Restore(recorded);
            foreach (var entry in restored)
            {
                Transcript.Add(entry);
            }

            // AC-1377: the host holds what was repainted, not the raw log — a row this build skipped, or one the log
            // left waiting on a prompt, must not come back the moment the host next touches it.
            _control.SeedTranscript([.. restored.Select(TranscriptSnapshot.Capture)]);
        }
        finally
        {
            _replayingRecordedTranscript = false;
        }
    }

    // AC-1090: rolls this pane's recorded conversation aside, for a launch that starts a new one rather than
    // continuing this one — a new conversation is a new log.
    public Task ArchiveRecordedTranscriptAsync(CancellationToken cancellationToken = default) =>
        _control.ArchiveRecordedTranscriptAsync(cancellationToken);

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

    // Distinct from `SessionStatus.NeedsAttention`, which is deliberately stickier: the host's attention flag is set when a
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
        _control = _BindControl(SessionControls.DesignTime);
        _rowFeed = _FollowRows(eventLog: null, logger: null);
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
        _control.Enqueue(new QueuedPrompt("run the tests once the build finishes", []));
    }

    public SessionViewModel(
        ISessionControlFactory sessionControls,
        IVoicePushToTalkService? voicePushToTalk = null,
        IVoiceSettingsStore? voiceSettingsStore = null,
        IVoicePlaybackQueue? voicePlaybackQueue = null,
        IOpenMicState? openMicState = null,
        IUsageHistory? usageHistory = null,
        IAgentTurnInboxDelivery? turnInboxDelivery = null,
        ISessionProviderNames? providerNames = null,
        IProviderUsageSignals? usageSignals = null,
        VoiceOverlayCoordinator? voiceOverlay = null,
        IProfileLoginChecker? loginChecker = null,
        ISessionLoginFlows? loginFlows = null,
        IMentionFileSource? mentionFileSource = null,
        ISessionTranscriptReader? transcriptReader = null,
        ILogger<SessionViewModel>? logger = null,
        IBackendEventLog? eventLog = null)
        : base(usageHistory)
    {
        _transcriptReader = transcriptReader;
        // AC-1090: every pane but the design-time/unit-test graph gets the store from the container, which
        // `APaneTakenFromTheContainer_RecordsItsRowsToDisk` holds this to; the control writes it (AC-1377, AC-1449).
        _control = _BindControl(sessionControls);

        _rowFeed = _FollowRows(eventLog, logger);
        _turnInboxDelivery = turnInboxDelivery;
        _providerNames = providerNames;
        _usageSignals = usageSignals;
        _loginChecker = loginChecker;
        _loginFlows = loginFlows;
        _mentionFileSource = mentionFileSource;
        _logger = logger;
        MentionPicker = new MentionPickerViewModel(_MentionPathsAsync, () => WorkingDirectory);
        _TrackPendingAttachments();
        InitializeVoice(voicePushToTalk, voiceSettingsStore, voicePlaybackQueue, openMicState, voiceOverlay);
        CloseRequested += (_, _) => _control.StopPolling();
    }

    // The control reports on the consumer's thread except for its clocks, which tick on the pool and are posted here.
    private ISessionControl _BindControl(ISessionControlFactory controls)
    {
        var host = controls.Create(() => PaneId);
        host.Capabilities = Capabilities;
        host.OutgoingText = prompt => BuildOutgoingText(prompt.Text, _RowOf(prompt.ReplyToRowId));
        // AC-1319: the host decides a mid-turn start on this pane's capabilities, which settle once the driver started.
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Capabilities))
            {
                host.Capabilities = Capabilities;
            }
        };
        // AC-1438: the host folds its runtime's events itself, on this thread, a frame's worth at a time (AC-529).
        host.PumpOn(UiPost, UiPostAfterWindow);
        host.Folded += fold => _InOrder(() => _OnFolded(fold));
        host.QueueChanged += (_, change) => _MirrorQueue(change);
        // AC-251: the session's working life starts with its runtime; whatever the launch waited on is setup, not work.
        host.Started += startedAt => _startedAt = startedAt;
        host.BusyChanged += _OnHostBusyChanged;
        host.TurnStarting += prompt =>
        {
            _HoldQueueIfExit(prompt);
            _InOrder(() => _OnTurnStarting(prompt));
        };
        host.TurnFailedToStart += (prompt, exception) => _InOrder(() => _OnTurnFailedToStart(prompt, exception));
        host.MailDelivered += notice => _InOrder(() => _NoteDeliveredMail(notice));
        host.LoginChecked += loggedIn => _OnUiThread(() => _WhilePolling(() => ReportLoginStatus(loggedIn)));
        host.UsageCatchUpDue += () => _OnUiThread(() => _WhilePolling(_RefreshLimits));
        host.SignOfLifeDue += arm => _OnUiThread(() => _OnSignOfLife(arm));
        // AC-1437: raised inside the host's fold, on this thread; AC-1438: drawn once the rows they name are.
        host.LiveStateChanged += state => _InOrder(() => _OnLiveStateChanged(state));
        host.TurnEnded += end => _InOrder(() => _OnTurnEnded(end));
        host.ToolProgressed += () => ToolActivity?.Invoke();
        host.BackgroundTaskNotified += notice => _InOrder(() => _OnBackgroundTaskNotified(notice));
        host.OutputTextProduced += RaiseOutputText;
        host.ToolActivityProduced += toolCall => RaiseToolActivity(toolCall.ToolName, toolCall.InputJson, toolCall.ResultContent, toolCall.IsError);
        return host;
    }

    // AC-1438: null in the design-time and unit-test graphs, which draw straight from the host and act on its signals as
    // they come; see `SessionRowFeed` for why a pane reading the log cannot.
    private readonly SessionRowFeed? _rowFeed;

    // AC-1438: every pane the container builds draws its rows from the backend event log, not from the host.
    private SessionRowFeed? _FollowRows(IBackendEventLog? eventLog, ILogger? logger)
    {
        if (eventLog is null)
        {
            _control.RowUpserted += upsert => _DrawHostRow(upsert.Row);
            return null;
        }

        return new SessionRowFeed(
            eventLog, () => PaneId, () => _control.Rows, _DrawHostRow, _Resync, UiPost,
            exception => logger?.LogError(exception, "The pane stopped reading its rows from the event log."));
    }

    // AC-1456: a session on a connect server, drawn from its handle's rows and live state, the same stream the server
    // group reads, so the two cannot drift. Sending and answering go to the server through the handle's control (AC-1469).
    // A pane over a session on a connect server; its control, one per pane, can launch nothing.
    internal static async Task<SessionViewModel> OverRemoteAsync(ISessionHandle handle, string server)
    {
        var pane = new SessionViewModel(SessionControls.DesignTime);
        await pane.FollowRemoteAsync(handle, server);
        return pane;
    }

    internal async Task FollowRemoteAsync(ISessionHandle handle, string server)
    {
        RemoteServer = server;
        _remoteControl = handle.Control;
        _remoteHandle = handle;
        OnPropertyChanged(nameof(AllowLabel));
        Title = handle.Title;
        Statusline = handle.Statusline;
        ActiveProfileLabel = handle.ActiveProfileLabel;
        SessionStatus = handle.SessionStatus;
        Status = $"Runs on {server}.";
        SetRemoteLink(isUp: false, mayAnswerPermissions: false, allowsForSession: false);

        Action<TranscriptRowUpsert> row = upsert => UiPost(() => _DrawHostRow(upsert.Row));
        Action<SessionLiveState> live = state => UiPost(() => _OnLiveStateChanged(state));
        handle.RowUpserted += row;
        handle.LiveStateChanged += live;
        _unfollowRemote = () =>
        {
            handle.RowUpserted -= row;
            handle.LiveStateChanged -= live;
        };

        if (await handle.ReadRowsAtAsync(() => 0).ConfigureAwait(true) is { Rows.Count: > 0 } snapshot)
        {
            _Resync(snapshot.Rows);
        }

        if (handle.LiveState is { } state && !ReferenceEquals(state, SessionLiveState.None))
        {
            _OnLiveStateChanged(state);
        }
    }

    // AC-1469: what the server group last said about the line and the key; the pane follows it and keeps no queue of its own.
    internal void SetRemoteLink(bool isUp, bool mayAnswerPermissions, bool allowsForSession)
    {
        IsLinkUp = isUp;
        MayAnswerPermissions = mayAnswerPermissions;
        AllowsForSession = allowsForSession;
        IsInputEnabled = isUp;
        ComposerPlaceholder = isUp
            ? $"Send a message to the session on {RemoteServer}…"
            : $"Reconnecting to {RemoteServer}…";
    }

    private async Task _SendToServerAsync()
    {
        if (_remoteControl is null || !IsLinkUp || (string.IsNullOrWhiteSpace(InputText) && PendingAttachments.Count == 0))
        {
            return;
        }

        if (PendingAttachments.Count > 0)
        {
            Transcript.Add(new TranscriptEntryViewModel(
                TranscriptEntryKind.Error, $"Images cannot be sent to a session on {RemoteServer}. Remove them and send again."));
            return;
        }

        var text = InputText;
        var replyTo = PendingReplyTo;
        InputText = string.Empty;
        PendingReplyTo = null;
        try
        {
            await _remoteControl.SendPromptAsync(BuildOutgoingText(text, replyTo)).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            if (string.IsNullOrEmpty(InputText))
            {
                InputText = text;
                PendingReplyTo ??= replyTo;
            }

            Transcript.Add(new TranscriptEntryViewModel(
                TranscriptEntryKind.Error, $"Not sent to {RemoteServer}: {exception.Message}"));
        }
    }

    // Closed before the call like the node path; a refusal (the key may not answer, the line is down) reopens the row.
    private async Task _AnswerOnServerAsync(TranscriptEntryViewModel entry, bool allow, string? answersJson, bool forSession = false)
    {
        if (_remoteControl is null || !entry.IsPendingPermission || entry.ToolUseId is null || !IsLinkUp || !MayAnswerPermissions)
        {
            return;
        }

        _answering.Add(entry);
        entry.IsPendingPermission = false;
        entry.PermissionDecision = $"Answering on {RemoteServer}…";
        try
        {
            // The by-id route says whether the server took the answer; a structured answer has no such route.
            var answered = true;
            if (answersJson is null && _remoteHandle is { } handle)
            {
                answered = await handle.RespondToPermissionByIdAsync(entry.ToolUseId, allow, forSession).ConfigureAwait(true);
            }
            else
            {
                await _remoteControl.RespondToPermissionAsync(entry.ToolUseId, allow, answersJson).ConfigureAwait(true);
            }

            entry.PermissionDecision = !answered ? $"Already answered on {RemoteServer}"
                : answersJson is not null ? "Answered" : !allow ? "Denied" : forSession ? "Allowed for this session" : "Allowed";
        }
        catch (Exception exception)
        {
            entry.PermissionDecision = $"Not answered — {exception.Message}";
            entry.IsPendingPermission = true;
        }
        finally
        {
            _answering.Remove(entry);
        }
    }

    private void _InOrder(Action signal)
    {
        if (_rowFeed is null)
        {
            signal();
        }
        else
        {
            _rowFeed.InOrder(signal);
        }
    }

    // T10: decided as the turn leaves, not once the pane draws it, so the host keeps the queue when that turn ends.
    private void _HoldQueueIfExit(QueuedPrompt prompt)
    {
        if (AutoCloseOnExit && prompt.Text.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
        {
            _control.EndsAfterThisTurn = true;
        }
    }

    // One frame at 30 fps, matching `MarkdownView.RebuildIntervalMs`: the markdown rows repaint on that cadence anyway,
    // so folding the deltas that arrive between two repaints removes work no one could have seen (AC-529).
    private const int PumpWindowMs = 33;

    // The host's pump on this thread. Posted first, timer armed second: `DispatcherTimer` binds to
    // `Dispatcher.CurrentDispatcher`, so arming one from the runtime's thread lands it on a dispatcher nothing pumps.
    internal static readonly Action<Action> UiPost = action => Dispatcher.UIThread.Post(action);

    internal static readonly Action<Action> UiPostAfterWindow =
        action => Dispatcher.UIThread.Post(() => DispatcherTimer.RunOnce(action, TimeSpan.FromMilliseconds(PumpWindowMs)));

    // The host's queue as chips, one per prompt and in its order; a chip's Remove takes its prompt out of the queue.
    private void _MirrorQueue(NotifyCollectionChangedEventArgs change)
    {
        switch (change.Action)
        {
            case NotifyCollectionChangedAction.Add when change.NewItems is { } added:
                var index = change.NewStartingIndex;
                foreach (var prompt in added.OfType<QueuedPrompt>())
                {
                    QueuedMessages.Insert(index++, _ChipFor(prompt));
                }

                break;

            case NotifyCollectionChangedAction.Remove when change.OldItems is { } removed:
                for (var count = 0; count < removed.Count; count++)
                {
                    QueuedMessages.RemoveAt(change.OldStartingIndex);
                }

                break;

            default:
                QueuedMessages.Clear();
                foreach (var prompt in _control.Queue)
                {
                    QueuedMessages.Add(_ChipFor(prompt));
                }

                break;
        }
    }

    private QueuedMessageViewModel _ChipFor(QueuedPrompt prompt) =>
        new(prompt, _RowOf(prompt.ReplyToRowId), chip => _control.Withdraw(chip.Prompt));

    // AC-935: the row a queued reply answers, while this pane still holds it.
    private TranscriptEntryViewModel? _RowOf(string? rowId) =>
        rowId is not null && _rowsById.TryGetValue(rowId, out var row) ? row : null;

    // A DispatcherTimer stopped on close ticked no more; a pool tick posted just before the close still lands here.
    private void _WhilePolling(Action action)
    {
        if (_control.IsPolling)
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
        get => _control.TurnsHeldBecause;
        set
        {
            if (_control.TurnsHeldBecause == value)
            {
                return;
            }

            _control.TurnsHeldBecause = value;
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
        if (_control.IsAttached)
        {
            return;
        }

        // AC-410: still set here for a restored pane's first launch — see _restoredOfferSnapshot's own doc for why
        // it has to be captured now rather than read again once the first turn actually completes.
        _restoredOfferSnapshot = RestoreOffer;

        // AC-713: what an auth-related error or the host's login poll check against.
        _profile = profile;

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
        _launchOptions = launchOptions;

        // AC-661: the same cap the runtime hands the OS, so the bar can warn on the approach to it.
        MemoryCapBytes = SessionMemoryCap.ResolveBytes(profile, _launchOptions);

        // The model dropdown lists Claude aliases (opus/sonnet/…), which are meaningless to a local provider — it uses
        // the model set on its profile, so only a Claude session is handed the selected one.
        var launchModel = profile?.Provider is null or SessionProvider.ClaudeCli ? SelectedModel.Value : null;

        // AC-218: ProjectId is set on this panel by CockpitViewModel before this runs, so the driver's MCP fan-out
        // resolves this project's registry view. Pre-approved tools (AC-215) are for a self-driving run.
        await StartWithProfileAsync(new SessionStart(
            profile, SelectedPermissionMode.Value, launchModel, McpServerSelection, workingDirectory, resume, _launchOptions,
            ProjectId, preApprovedTools, preApproveAllTools));

        // The runtime is left un-started when the CLI never came up. Unlock and reset the mode so a failed bypass
        // launch doesn't strand the panel on a phantom, disabled "Bypass permissions" with no session.
        if (!_control.IsRunning)
        {
            // AC-1239: the quieter half — StartAsync returned without throwing and nothing is running, which used to
            // leave Status reading "Session started." on a session that never did. A launch that threw already spoke.
            // Only with a runtime in hand: a null one means no launch was attempted (the design-time graph) or that a
            // teardown took it mid-start, and neither of those failed to start.
            if (StartFailure is null && _control.IsAttached)
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
        if (!_control.IsAttached)
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
            [.. _control.PreApprovedTools],
            _control.PreApprovesAllTools);
    }

    // Everything that described the conversation just dropped (AC-564). The turn's live state and the queue aimed
    // at it go because the turn they belonged to is over; the numbers go because "ctx 66%" left standing over an
    // empty context is a figure that actively lies (decision 3). The transcript is deliberately not among them.
    private void _ResetForNewConversation()
    {
        _control.ClearQueue();
        _control.ResetTranscriptStreaming();
        _currentTurnAssistantEntries.Clear();
        _readAloudFlushedLength = 0;
        _spokenSomethingThisTurn = false;
        _StopSignOfLifeClock();
        ClearCurrentTurnImages();
        IsBusy = false;
        _control.ResetLiveState();

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

        var name = _providerNames?.DisplayNameOf(plugin.ProviderId);
        return string.IsNullOrWhiteSpace(name) ? string.Empty : name;
    }

    private async Task StartWithProfileAsync(SessionStart start)
    {
        // A host that cannot launch (the design-time graph) still arms its login poll and pre-approvals, as it always did.
        if (!_control.CanLaunch)
        {
            await _control.StartAsync(start);
            return;
        }

        var profile = start.Profile;
        var workingDirectory = start.WorkingDirectory;
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
            if (await _control.StartAsync(start) is not { } launched)
            {
                return;
            }

            // AC-701: the driver polls usage during StartAsync for every session, resumed or fresh — pull the
            // figures in now rather than leaving the header pill empty until the first turn completes. A driver
            // with nothing yet reads null and _RefreshLimits leaves the bars as they were.
            _RefreshLimits();

            // The process the meter weighs (#78) exists only once the driver started it.
            ProcessId = launched.ProcessId;

            // Capabilities (notably SupportsTools) only settle once the driver has actually started.
            if (launched.Capabilities is { } capabilities)
            {
                Capabilities = capabilities;
            }

            // The provider's generic live controls (#45 D4) settle at the same moment as capabilities — the driver
            // lists them once its session is up (Codex resolves its model list on start) — so read them here too.
            _PopulateLiveControls(launched.LiveOptions);

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
                await _control.SetAutoApproveToolsAsync(true);
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
        _ = _control.SetAutoApproveToolsAsync(value);
    }

    // Live-switches the running session's permission mode. No-op before the session has started.
    partial void OnSelectedPermissionModeChanged(PermissionModeOption value)
    {
        if (!_control.IsRunning)
        {
            return;
        }

        _ = _SetPermissionModeSafeAsync(value.Value);
    }

    // Live-switches the running session's model. No-op before the session has started.
    partial void OnSelectedModelChanged(ModelOption value)
    {
        if (!_control.IsRunning)
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
        if (!_control.IsRunning)
        {
            return;
        }

        _ = _SetMaxThinkingTokensSafeAsync(value.MaxThinkingTokens);
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (!_control.IsRunning)
        {
            return;
        }

        try
        {
            await _control.InterruptAsync();
            Status = "Interrupted.";

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
        if (!_control.IsRunning || TurnsHeldBecause is not null || !Capabilities.SupportsContextCompaction)
        {
            return false;
        }

        IsBusy = true;

        try
        {
            await _control.CompactContextAsync();
            return true;
        }
        catch (Exception ex)
        {
            // The turn never left, so the session is not working — left standing, it would read as permanently busy.
            IsBusy = false;
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
        if (!_control.IsAttached)
        {
            return;
        }

        try
        {
            // AC-409: the control records the mode once the switch took.
            await _control.SetPermissionModeAsync(mode);
        }
        catch (Exception ex)
        {
            Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, $"Permission-mode switch failed: {ex.Message}"));
        }
    }

    private async Task _SetModelSafeAsync(string model)
    {
        if (!_control.IsAttached)
        {
            return;
        }

        try
        {
            await _control.SetModelAsync(model);
        }
        catch (Exception ex)
        {
            Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, $"Model switch failed: {ex.Message}"));
        }
    }

    private async Task _SetMaxThinkingTokensSafeAsync(int maxThinkingTokens)
    {
        if (!_control.IsAttached)
        {
            return;
        }

        try
        {
            await _control.SetMaxThinkingTokensAsync(maxThinkingTokens);
        }
        catch (Exception ex)
        {
            Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Error, $"Effort switch failed: {ex.Message}"));
        }
    }

    // Rebuilds the generic live-control panel from the running driver's declared options (#45 D4).
    private void _PopulateLiveControls(IReadOnlyList<SessionLiveOption> options)
    {
        LiveControls.Clear();
        foreach (var option in options)
        {
            LiveControls.Add(new LiveControlViewModel(option, _SetLiveOptionSafeAsync));
        }
    }

    // Live-switches one of the provider's generic controls on the running session's driver (#45 D4).
    private async Task _SetLiveOptionSafeAsync(string key, string value)
    {
        if (!_control.IsAttached)
        {
            return;
        }

        try
        {
            await _control.SetLiveOptionAsync(key, value);
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
        if (!_control.IsRunning || !CanPasteImages)
        {
            return false;
        }

        IReadOnlyList<Core.Sessions.ImageAttachment> images = [Core.Sessions.ImageAttachment.FromBytes(screenshotPng, "image/png")];

        await _control.SubmitAsync(new QueuedPrompt(caption, images));
        return true;
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) && PendingAttachments.Count == 0)
        {
            return;
        }

        if (RemoteServer is not null)
        {
            await _SendToServerAsync();
            return;
        }

        // Sending before the session has started reaches the CLI process before its I/O is wired and surfaces a raw
        // "Start must be called before I/O" error (#16).
        if (!_control.IsRunning)
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
        await _control.SubmitAsync(new QueuedPrompt(text, images, replyTo?.Id), Capabilities.SupportsMidTurnInput);
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

        // Taken out the way its chip's own Remove takes it, so the host's queue and the chips stay one list.
        var last = QueuedMessages[^1];
        last.RemoveCommand.Execute(null);

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
        var replyTo = _RowOf(prompt.ReplyToRowId);

        // "exit" closes the session once its turn completes when the operator enabled it (T10). The
        // message is still sent normally so any session-end/Stop-hooks on Claude's side run first; the
        // close then fires when the turn ends. Armed at dispatch so a queued "exit" counts too.
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

        // The host marks the turn busy and clears a stale interrupt itself (AC-1031) once this returns.
        _lastDispatchedUserTurn = (text, images);

        // Cleared when the turn completes, or in `_OnTurnFailedToStart` if the send never happened (AC-116).
        _RememberTurnImages(images);
    }

    // The host frees the session itself once this returns.
    private void _OnTurnFailedToStart(QueuedPrompt prompt, Exception exception)
    {
        ClearCurrentTurnImages();
        Transcript.Add(new TranscriptEntryViewModel(
            TranscriptEntryKind.Error, SendFailureMessage(exception, _control.IsRunning)));
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

        SetCurrentTurnImages(images);
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

    // AC-1476: remote only, and only where the server said it keeps the rule; the button is drawn on that same test.
    [RelayCommand]
    private async Task AllowForSessionToolAsync(TranscriptEntryViewModel entry)
    {
        if (CanAllowForSession)
        {
            await _AnswerOnServerAsync(entry, allow: true, answersJson: null, forSession: true);
        }
    }

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

        if (RemoteServer is not null)
        {
            await _AnswerOnServerAsync(entry, allow, answersJson);
            return;
        }

        if (!_control.IsAttached || entry.ToolUseId is null)
        {
            return;
        }

        _MarkDecided(entry, answersJson is not null ? "Answered" : allow ? "Allowed" : "Denied");
        await _control.RespondToPermissionAsync(entry.ToolUseId, allow, answersJson);
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
    internal async Task<bool> RespondToPermissionByIdAsync(string toolUseId, bool allow, bool forSession = false)
    {
        if (PendingToolPermissionRows().FirstOrDefault(row => string.Equals(row.ToolUseId, toolUseId, StringComparison.Ordinal)) is not { } entry)
        {
            return false;
        }

        // AC-1476: a session-wide allow is the pane's own exact "Always".
        await (forSession && allow && entry.ToolName is not null && entry.NodePermission is null ? AllowAlwaysAsync(entry, PermissionRuleScope.Exact) : RespondToPermissionAsync(entry, allow));
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
        if (!_PendingRows().Any())
        {
            _control.ClearNeedsAttention();
        }
    }

    private async Task AllowAlwaysAsync(TranscriptEntryViewModel entry, PermissionRuleScope scope)
    {
        if (RemoteServer is not null || !_control.IsAttached || entry.ToolUseId is null || entry.ToolName is null || entry.NodePermission is not null)
        {
            return;
        }

        _MarkDecided(entry, scope == PermissionRuleScope.Wildcard
            ? $"Always allowed ({entry.ToolName}:*)"
            : $"Always allowed (exact: {entry.ToolName})");

        await _control.AllowPermissionAlwaysAsync(entry.ToolUseId, entry.ToolName, entry.InputJson ?? "{}", scope);

        // AC-1476: a call already waiting on the same rule is answered with it.
        var rule = scope == PermissionRuleScope.Wildcard
            ? PermissionRule.ForWildcard(entry.ToolName)
            : PermissionRule.ForExact(entry.ToolName, entry.InputJson ?? "{}");
        foreach (var other in PendingToolPermissionRows().Where(other => other != entry && other.ToolName is not null
            && other.NodePermission is null && rule.Matches(other.ToolName, other.InputJson ?? "{}")).ToList())
        {
            if (other.ToolUseId is { } otherId)
            {
                _MarkDecided(other, "Allowed for this session");
                await _control.RespondToPermissionAsync(otherId, allow: true, answersJson: null);
            }
        }
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

        _control.RestartSignOfLife(AssistantSpokenFillers.SignOfLifeDelay(_signOfLifeRepeat));
    }

    // One tick of the host's clock, on the UI thread; an arm a restart or stop has since replaced says nothing.
    private void _OnSignOfLife(int arm)
    {
        if (!_control.IsCurrentSignOfLife(arm))
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

        _control.RestartSignOfLife(AssistantSpokenFillers.SignOfLifeDelay(_signOfLifeRepeat));
    }

    private void _StopSignOfLifeClock()
    {
        _control.StopSignOfLife();
        _signOfLifeRepeat = 0;
    }

    // Raised when the session makes real tool progress — a tool call surfacing or a tool result landing (AC-215/stall).
    // An embedder that fails a silent step on a stall deadline (Autopilot) resets that deadline on this, so a step that
    // is slow because it is working hard is not mistaken for a stuck one. Not raised on text/thinking on purpose.
    public event Action? ToolActivity;

    // A turn the host's fold ended in this pass; the pane reads its limits once it is done with the event.
    private bool _turnCompletedInFold;

    // The connection, usage and tool calls this pane last drew, so a new reading is told from one it already showed.
    private SessionConnection? _shownConnection;
    private SessionUsageTotals _shownUsage = SessionUsageTotals.None;
    private IReadOnlyList<SessionActiveToolCall> _shownToolCalls = [];

    // AC-1449: what `SessionControls.Apply`, the seam tests and renders drive in place of a runtime, pumps through.
    internal ISessionControl Control => _control;

    // AC-1437/1438: the host folds the event, its rows and what `_OnLiveStateChanged` and `_OnTurnEnded` draw, and ends
    // the turn itself; what is left here is what this pane does with the row the event landed on.
    private void _OnFolded(TranscriptFold fold)
    {
        var landed = _RowOf(fold.Row?.Id);

        // AC-146: a sub-agent's text, in its lane or orphaned, is never the reply that is read aloud.
        if (fold.IsReply)
        {
            _currentTurnAssistantEntries.AddRange(_addedByFold);
        }

        // A result coupled to its call (L14) is where a backgrounded call learns its task id, in any lane.
        if (landed is { Kind: TranscriptEntryKind.ToolUse, BackgroundTaskId: not null } && !_backgroundToolRows.Contains(landed))
        {
            _TrackBackgroundToolRow(landed);
        }

        if (fold.AsksTheOperator)
        {
            if (fold.Row is { IsPendingPermission: true } && landed is { } entry)
            {
                // AC-715: an AskUserQuestion rides the permission callback but asks for an answer, not consent, so
                // the row renders its questions as choices instead of Allow/Deny over raw JSON.
                entry.QuestionPrompts = entry.ToolName == AskUserQuestionToolName
                    ? AskUserQuestionViewModel.Parse(entry.InputJson ?? "{}")
                    : null;
                // AC-532: the composer's activity band now reads "waiting for permission" rather than "running".
                _RaiseActiveToolActivityChanged();
            }

            // A question or a prompt pauses the turn, so speak what was said before it now (AC-97).
            _FlushPendingProseForReadAloud();
        }

        _addedByFold.Clear();
        if (!_turnCompletedInFold)
        {
            return;
        }

        _turnCompletedInFold = false;
        _RefreshLimits();

        // "exit" turn finished → ask the cockpit to close this session (T10); the host kept what was queued.
        if (_closeAfterTurn)
        {
            _closeAfterTurn = false;
            _control.EndsAfterThisTurn = false;
            RaiseCloseRequested();
        }
    }

    // AC-1438: the host's fold as this pane last drew it, and its signals once drawn, for the pane's registry handle.
    internal SessionLiveState LiveState { get; private set; } = SessionLiveState.None;

    internal event Action<SessionLiveState>? LiveStateChanged;

    internal event Action<SessionTurnEnd>? TurnEnded;

    internal event Action<SessionBackgroundTaskNotice>? BackgroundTaskNotified;

    private void _OnTurnEnded(SessionTurnEnd end)
    {
        _DrawTurnEnd(end);
        TurnEnded?.Invoke(end);
    }

    // AC-1437: the turn-end half of what `Apply` did per event; the status, usage and outstanding work it left behind
    // arrive after this, in `_OnLiveStateChanged`.
    private void _DrawTurnEnd(SessionTurnEnd end)
    {
        var failedRow = end.FailedRowId is { } failedRowId && _rowsById.TryGetValue(failedRowId, out var row) ? row : null;
        if (end.BySessionError)
        {
            // AC-713: re-checks the profile's own login gate rather than pattern-matching the message.
            if (failedRow is not null && _profile is not null && _loginChecker?.IsLoggedIn(_profile) == false)
            {
                failedRow.ActionLabel = "Login";
                failedRow.ActionCommand = new RelayCommand(() => _StartLoginFlow(failedRow));
            }

            // A session error ends the turn without completing it, so its images go here too (AC-116).
            ClearCurrentTurnImages();
            return;
        }

        if (failedRow is not null)
        {
            // AC-939: an auth classification means Retry would just fail again, so it offers the login gate instead.
            if (failedRow.ErrorKind == SessionErrorKind.AuthRequired && _profile is not null && _loginChecker?.IsLoggedIn(_profile) == false)
            {
                failedRow.ActionLabel = "Login";
                failedRow.ActionCommand = new RelayCommand(() => _StartLoginFlow(failedRow));
            }
            // AC-728: unset when this turn never went through the host's dispatch, which only a scheduled resume's
            // own first turn (SendPromptAsync, AC-410) does.
            else if (_lastDispatchedUserTurn is { } lastTurn)
            {
                failedRow.ActionLabel = "Retry";
                failedRow.ActionCommand = new RelayCommand(
                    () => _control.DispatchInBackground(new QueuedPrompt(lastTurn.Text, lastTurn.Images)));
            }
        }

        // A failure here is a resume that was tried and refused: an expired conversation id ends the turn as
        // error_during_execution with no Result (AC-410). AC-1031: an interrupted first turn is not a refused resume.
        if (_restoredOfferSnapshot is { } restoredOffer)
        {
            _restoredOfferSnapshot = null;
            if (end.IsError && !end.WasInterrupted)
            {
                RestoreOffer = restoredOffer with
                {
                    Availability = SessionRestoreAvailability.Gone,
                    Explanation = _DegradedTurnExplanation(end, restoredOffer.State?.WorkingDirectory),
                };
            }
        }

        _FlushPendingProseForReadAloud();

        _currentTurnAssistantEntries.Clear();
        _readAloudFlushedLength = 0;
        _spokenSomethingThisTurn = false;
        _StopSignOfLifeClock();
        // This turn's images belong to this turn only (AC-116): a later image-less turn's tool call attaches nothing stale.
        ClearCurrentTurnImages();
        _turnCompletedInFold = true;
    }

    // AC-1437: the host's fold, drawn. Status last, as it always came after what it weighs.
    private void _OnLiveStateChanged(SessionLiveState state)
    {
        _cliSessionId = state.CliSessionId;
        if (state.Connection is { } connection && !ReferenceEquals(connection, _shownConnection))
        {
            _shownConnection = connection;
            _ShowConnection(connection);
        }

        if (!ReferenceEquals(state.ActiveToolCalls, _shownToolCalls))
        {
            _ShowActiveToolCalls(state.ActiveToolCalls);
        }

        // Replaced wholesale, an empty list included, which is how the last task ending arrives (AC-276).
        if (!ReferenceEquals(state.BackgroundTasks, _backgroundTasks))
        {
            _backgroundTasks = state.BackgroundTasks;
            OnPropertyChanged(nameof(HasOutstandingBackgroundShells));
            OnPropertyChanged(nameof(SessionStatusLabel));
            _RebuildBackgroundTaskRows();
        }

        // Recorded after every turn (AC-251); a conversation started over blanks the meter in `_ResetForNewConversation`.
        if (state.Usage != _shownUsage)
        {
            _shownUsage = state.Usage;
            if (state.Usage.Turns > 0)
            {
                _usage.Totals = state.Usage;
                HasUsage = _usage.HasData;
                UsageSummary = _usage.Summary;
                UsageTooltip = _usage.Tooltip;
                _RecordUsageSnapshot();
            }
        }

        SessionStatus = state.Status;
        LiveState = state;
        LiveStateChanged?.Invoke(state);
    }

    // The init event is where an SDK session's working directory becomes known; surfaced on the shared base so the
    // read/observe surface can report it (a directory-scoped plugin follows this).
    private void _ShowConnection(SessionConnection connection)
    {
        if (!string.IsNullOrEmpty(connection.WorkingDirectory))
        {
            WorkingDirectory = connection.WorkingDirectory;
        }

        // AC-537: the tool count said nothing an operator could act on, and cwd duplicated the folder icon's own
        // tooltip (SessionHeaderBar.axaml) (AC-563).
        Status = ConnectedStatusLine;
        ConnectedToolsHeading = connection.Tools.Count == 0
            ? "No tools connected — add an MCP server (e.g. filesystem) to give this session tools."
            : $"{connection.Tools.Count} tools connected";

        // AC-963: the same hover that lists the servers says what became of their tools, preloaded or kept out of the
        // prompt behind search_tools. Only the init event knows which of the two happened.
        McpToolReach = McpToolReachFor(connection.Tools);

        // Seed it in, don't fire a switch: the driver already reported this, and set_model would be the host talking
        // back a choice the operator never made (AC-141).
        if (connection.Model is { Length: > 0 } resolvedModel)
        {
            foreach (var control in LiveControls)
            {
                if (control.Key == WellKnownPluginSessionOptions.Model)
                {
                    control.SeedIfUnset(resolvedModel);
                }
            }
        }
    }

    // AC-532: a call keeps the label its row had when it surfaced ("Bash  ·  dotnet build"), the row's own ToolHeader.
    private void _ShowActiveToolCalls(IReadOnlyList<SessionActiveToolCall> calls)
    {
        var surfaced = calls.Any(call => _activeToolCalls.TrueForAll(shown => shown.ToolUseId != call.ToolUseId));
        var shown = _activeToolCalls.ToList();
        _shownToolCalls = calls;
        _activeToolCalls.Clear();
        _activeToolCalls.AddRange(calls.Select(call =>
            shown.FindIndex(existing => existing.ToolUseId == call.ToolUseId) is var index and >= 0
            ? shown[index]
            : new ActiveToolCall(
                call.ToolUseId,
                Transcript.LastOrDefault(row => row.ToolUseId == call.ToolUseId)?.ToolHeader ?? call.ToolName,
                call.StartedAt)));
        _RaiseActiveToolActivityChanged();
        if (!surfaced)
        {
            return;
        }

        // Until then the only mid-turn flushes were a permission prompt and a question, which stopped being enough
        // the moment an operator turned on bypassPermissions or the cockpit's consent bypass (AC-575).
        _FlushPendingProseForReadAloud();

        // AC-597: and when there was no lead-in to flush, say one of our own.
        _SpeakLeadInIfTheModelGaveNone();

        // AC-598: the wait starts here too, so a turn that spends minutes in tools still gives a sign of life.
        _RestartSignOfLifeClock();
    }

    // AC-1057: the provider's own verdict on one background task, replacing the inferred done/failed guess for exactly
    // the row that started it; matched by ToolUseId first, by task id for a row whose ToolUseId went unset.
    private void _OnBackgroundTaskNotified(SessionBackgroundTaskNotice notice)
    {
        var notifiedRow = notice.ToolUseId is not null
            ? _backgroundToolRows.FirstOrDefault(row => row.ToolUseId == notice.ToolUseId)
            : null;
        notifiedRow ??= _backgroundToolRows.FirstOrDefault(row => row.BackgroundTaskId == notice.TaskId);
        if (notifiedRow is not null)
        {
            notifiedRow.BackgroundNotificationStatus = notice.Status;
        }

        BackgroundTaskNotified?.Invoke(notice);
    }

    // Replaced wholesale rather than added to and removed from: the session restates the complete set every time, so
    // a dropped update costs one stale reading instead of permanently desynchronising a ledger.
    private IReadOnlyList<BackgroundTask> _backgroundTasks = [];

    // True while a backgrounded shell is still running (AC-276). It does not hold the status — a never-ending
    // dev server would pin the session forever — but it does suppress the "session finished" notification, which
    // would otherwise announce a session that is still doing something.
    public override bool HasOutstandingBackgroundShells =>
        _backgroundTasks.Any(task => task.Kind == BackgroundTaskKind.Shell) || base.HasOutstandingBackgroundShells;

    // Write the running totals to the usage trail after every turn (AC-251), so they outlive the session and the app —
    // recording only at the end would lose exactly the run that crashed, which is the case worth measuring.
    private protected override (UsageRunKind RunKind, string? RunId, string? RunLabel, string? Model) GetUsageSnapshotMetadata() =>
        (RunKind, RunId, RunLabel, SelectedModel.Value);

    public override async Task<bool> SendPromptAsync(string prompt)
    {
        // A runtime whose driver never came up is still held by the pane, and it accepts a send and hands back a
        // completed task with nothing having gone anywhere.
        if (!_control.IsRunning || !CanTakeAPrompt)
        {
            return false;
        }

        // A turn started from here is as real as one the operator typed, and the rest of the cockpit only learns that
        // from these flags: the composer queues behind IsBusy rather than sending on top of a running turn, and
        // AC-395's wake refuses a pane that is already working.
        IsBusy = true;

        try
        {
            // Through the host's one funnel, as the composer's own sends are: a scheduled resume is a real turn on a real
            // session, so mail waiting for this pane belongs on it just as much.
            await _control.SendPromptAsync(prompt);
        }
        catch
        {
            // The turn never left, so the session is not working — left standing, it would read as permanently busy:
            // the composer would queue forever and no later message could ever wake it. Rethrown rather than swallowed,
            // because the callers already decide what a failed prompt means for them.
            IsBusy = false;
            throw;
        }

        return true;
    }

    // AC-539: that reason names the id but not what decides whether it can be found — Claude keeps its saved
    // conversations per working directory, so a pane that came back somewhere else gets the message with nothing
    // pointing at the cause (AC-410).
    private static string _DegradedTurnExplanation(SessionTurnEnd end, string? workingDirectory)
    {
        var reason = end.FailureReason ?? $"Claude could not resume the earlier conversation ({end.Subtype}).";

        return workingDirectory is { Length: > 0 } directory
            ? $"{reason}\nThe resume was made in {directory} — Claude keeps its conversations per working directory, so one saved elsewhere is not found here."
            : reason;
    }

    // --- Login flow (AC-713) ----------------------------------------------------------------------------------

    // "Sign in again" on the panel-wide auth-expiry bar: unlike the reactive row (below), there is no existing
    // row to expand into — the mockup's own answer is to open one, so there is still exactly one place a login
    // flow ever plays out, regardless of where it started.
    protected override void OnSignInAgainRequested()
    {
        // AC-720: a turn-status row, not an Error row — this is a status line, not a driver failure, and Error rows
        // now render as a severity-coloured card that would misread "Signing in again…" as a problem.
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.TurnCompleted, "Signing in again…");
        Transcript.Add(entry);
        _StartLoginFlow(entry);
    }

    // Starts an in-app login attempt and shows it inline on `entry`, replacing whatever action button asked for
    // it (`TranscriptEntryViewModel.HasAction` hides itself once `LoginFlow` is set).
    private void _StartLoginFlow(TranscriptEntryViewModel entry)
    {
        if (_profile is null || _loginFlows?.StartLogin(_profile, CancellationToken.None) is not { } flow)
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
        var status = _control.ReadUsageStatus(_profile?.ProviderConfig);
        if (status is not { HasAny: true })
        {
            return;
        }

        var providerId = _profile?.ProviderConfig is PluginProviderConfig plugin ? plugin.ProviderId : null;
        var declared = providerId is not null ? _usageSignals?.UsageSignalsOf(providerId) : null;
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

        _rowFeed?.Dispose();
        _unfollowRemote?.Invoke();
        await _StopRuntimeAsync();

        // AC-713, AC-786: the host's clocks stop here — the sign of life's own !IsBusy guard never fires once the
        // runtime is torn down mid-turn — and a running flow's subprocess must not outlive the pane that started it.
        await _control.DisposeAsync();
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
        _control.StopListening();

        // AC-529: ahead of the null guard, because a teardown that finds the runtime already gone still has the last
        // window's events queued.
        if (_control.HasPumpedWork)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                _control.FlushPumped();
            }
            else
            {
                Dispatcher.UIThread.Post(_control.FlushPumped);
            }
        }

        if (!_control.IsAttached)
        {
            return;
        }

        // The host clears its runtime before its first await, so readiness reads false by the time this is raised.
        var stopping = _control.StopAsync();
        OnPropertyChanged(nameof(IsSessionReady));
        await stopping;
    }
}
