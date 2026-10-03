using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Plugins.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// When a session may start its next turn: the turn in flight, the hold, and the end of a turn (AC-1376).
/// </summary>
public interface ISessionTurnGate
{
    /// <summary>
    /// True while a turn is in flight.
    /// </summary>
    bool IsBusy { get; set; }

    /// <summary>
    /// Why no new turn may start (AC-1321), else null.
    /// Lifting it while no turn runs sends what was queued behind the hold.
    /// </summary>
    string? TurnsHeldBecause { get; set; }

    /// <summary>
    /// Called by the host's own pump when it folds a TurnCompleted, in stream order (AC-1438).
    /// It clears the turn in flight and sends the next queued prompt, unless turns are held.
    /// </summary>
    void CompleteTurn();
}

/// <summary>
/// The rows a session's events form, as upserts of the row model the transcript store keeps (AC-1377).
/// </summary>
public interface ISessionTranscript
{
    /// <summary>
    /// Raised on the consumer's thread for every row a fold or a recorded row changed, in the order they changed.
    /// </summary>
    event Action<TranscriptRowUpsert>? RowUpserted;

    /// <summary>
    /// Called by the host's own pump for every event its runtime raises, in stream order (AC-1438).
    /// It forms or updates the rows that event touches on the consumer's thread, and returns the row it landed on.
    /// </summary>
    TranscriptFold ApplyToTranscript(SessionEvent evt);

    /// <summary>
    /// Records a row the consumer formed or changed itself, so the host's rows and the store stay one transcript.
    /// </summary>
    void RecordRow(TranscriptSnapshotEntry row);
}

// One runtime event as the host re-issues it, numbered on one counter for the whole backend so a stream can resume
// from a `Last-Event-ID` without renumbering (F5).
public readonly record struct SessionHostEvent(long Seq, SessionEvent Event);

// AC-1376/1438: one SDK session's backend half: runtime, turn gate and queue, the send funnel, the clocks, and the one
// consumer of its runtime's events. Members run on the consumer's thread (`PumpOn`) and awaits resume there (no
// ConfigureAwait(false)); only the timer events arrive on the thread pool. AC-1449: the desktop pane's control.
public sealed class SessionHost : ISessionTurnGate, ISessionTranscript, ISessionControl
{
    // Same as `ClaudeLoginStatus.MaxAge`: a tick mostly re-reads a cache another poll already refreshed (AC-713).
    public static readonly TimeSpan LoginPollInterval = TimeSpan.FromMinutes(1);

    // AC-761 F3: catches a usage reply that missed its turn's publish grace, for a session that has since gone idle.
    public static readonly TimeSpan UsageCatchUpInterval = TimeSpan.FromSeconds(30);

    // `TranscriptEntryKind.ToolUse` as the transcript store spells it, like `SessionTranscriptBuilder`'s kinds.
    private const string ToolUseKind = "ToolUse";

    // Long enough for a send the stopped runtime is failing to settle, short enough that a hung one cannot hold a close.
    private static readonly TimeSpan InFlightDrainBudget = TimeSpan.FromSeconds(5);

    private readonly Func<string> _paneId;
    private readonly ISessionManager? _manager;
    private readonly TimeProvider _time;
    private readonly IAgentTurnInboxDelivery? _turnInboxDelivery;
    private readonly IProfileLoginChecker? _loginChecker;
    private readonly ISharedUsageCache? _sharedUsageCache;
    private readonly ILogger? _logger;
    private readonly SessionStateRecorder? _stateRecorder;

    // AC-1090: Cockpit's own copy of the conversation, written from here so a session without a view has one too.
    private readonly ISessionTranscriptStore? _transcriptStore;
    private readonly SessionTranscriptBuilder _transcript;
    private readonly SessionLiveStateFold _liveState = new();
    private SessionLiveState _raisedLiveState = SessionLiveState.None;

    // The sends and wire answers started where nothing can await them (a hold lifting, a turn ending), kept so the
    // host's own disposal does.
    private readonly List<Task> _inFlight = [];

    private bool _isBusy;
    private string? _turnsHeldBecause;
    private IReadOnlySet<string> _preApprovedTools = new HashSet<string>(StringComparer.Ordinal);

    // Inline on the runtime's thread until a consumer names its own (`PumpOn`); the batch, when it asked for one.
    private Action<SessionEvent> _deliver;
    private SessionEventQueue? _events;

    private ITimer? _loginPoll;
    private SessionProfile? _loginProfile;
    private ITimer? _usageCatchUp;
    private ITimer? _signOfLife;
    private int _signOfLifeArm;

    // `paneId` is read on every turn rather than once: a pane adopts its id after it is built (`AdoptPaneId`).
    public SessionHost(
        Func<string> paneId,
        ISessionManager? manager,
        TimeProvider time,
        IAgentTurnInboxDelivery? turnInboxDelivery = null,
        IProfileLoginChecker? loginChecker = null,
        ISharedUsageCache? sharedUsageCache = null,
        ILogger? logger = null,
        ISessionTranscriptStore? transcriptStore = null,
        SessionStateRecorder? stateRecorder = null)
    {
        _paneId = paneId;
        _manager = manager;
        _time = time;
        _turnInboxDelivery = turnInboxDelivery;
        _loginChecker = loginChecker;
        _sharedUsageCache = sharedUsageCache;
        _logger = logger;
        _transcriptStore = transcriptStore;
        _stateRecorder = stateRecorder;
        _transcript = new SessionTranscriptBuilder(time, TryAutoAllow, () => InterruptRequested, _OnRowChanged);
        _deliver = Pump;
    }

    public event Action<SessionHostEvent>? EventAppended;

    public event Action<TranscriptRowUpsert>? RowUpserted;

    public event Action? BusyChanged;

    // Raised before a turn's send, so whatever the consumer echoes lands ahead of anything the turn produces.
    public event Action<QueuedPrompt>? TurnStarting;

    // Raised when a turn never left; the host has already given back any mail it would have carried.
    public event Action<QueuedPrompt, Exception>? TurnFailedToStart;

    // AC-394: the mail a turn carried, which entered the context without the operator typing or seeing it.
    public event Action<AgentInboxTurnNotice>? MailDelivered;

    public event Action<bool>? LoginChecked;

    public event Action? UsageCatchUpDue;

    // Carries the arm the tick belongs to; see `IsCurrentSignOfLife`.
    public event Action<int>? SignOfLifeDue;

    // AC-1438: what the fold of one pumped event left, raised after its other signals, as `SessionViewModel.Apply`
    // acted on it after the fold.
    public event Action<TranscriptFold>? Folded;

    // AC-1437: the fold's signals, raised on the consumer's thread like `RowUpserted`; see `ISessionHandle`.
    public event Action<SessionLiveState>? LiveStateChanged;

    public event Action<SessionTurnEnd>? TurnEnded;

    public event Action? ToolProgressed;

    public event Action<SessionBackgroundTaskNotice>? BackgroundTaskNotified;

    public event Action<string>? OutputTextProduced;

    public event Action<SessionToolCall>? ToolActivityProduced;

    public ISessionRuntime? Runtime { get; private set; }

    // What the consumer knows of the provider before its runtime says so; the runtime's own word otherwise.
    public SessionCapabilities? Capabilities { get; set; }

    public SessionLiveState LiveState => _liveState.Snapshot(IsBusy);

    // Whether this host can launch at all; the design-time graph builds one without a manager.
    public bool CanLaunch => _manager is not null;

    public bool IsAttached => Runtime is not null;

    public bool IsRunning => Runtime is { IsRunning: true };

    // Mutated on the consumer's thread only; the desktop pane mirrors it into its chips.
    public ObservableCollection<QueuedPrompt> Queue { get; } = [];

    IReadOnlyList<QueuedPrompt> ISessionControl.Queue => Queue;

    public event NotifyCollectionChangedEventHandler? QueueChanged
    {
        add => Queue.CollectionChanged += value;
        remove => Queue.CollectionChanged -= value;
    }

    public void Enqueue(QueuedPrompt prompt) => Queue.Add(prompt);

    public bool Withdraw(QueuedPrompt prompt) => Queue.Remove(prompt);

    public void ClearQueue() => Queue.Clear();

    // AC-935: the text a prompt leaves with, read when it leaves rather than when it was queued; a consumer that sends
    // more than it shows, such as a reply's citation, says so here.
    public Func<QueuedPrompt, string> OutgoingText { get; set; } = prompt => prompt.Text;

    // T10: the turn in flight is the session's last; when it completes, what was queued behind it stays queued.
    public bool EndsAfterThisTurn { get; set; }

    // AC-145: drain the whole queue into one follow-up turn instead of one turn per queued prompt.
    public bool CombineQueued { get; set; }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value)
            {
                return;
            }

            _isBusy = value;
            BusyChanged?.Invoke();
            _RaiseLiveStateIfChanged();
        }
    }

    // Read at the one funnel every turn goes through, so no starter can miss it; the running turn finishes, and what
    // was queued behind it waits here until the hold lifts.
    public string? TurnsHeldBecause
    {
        get => _turnsHeldBecause;
        set
        {
            if (_turnsHeldBecause == value)
            {
                return;
            }

            _turnsHeldBecause = value;
            if (value is null && !IsBusy)
            {
                _DispatchNextQueued();
            }
        }
    }

    public bool PreApprovesAllTools { get; private set; }

    public IReadOnlyCollection<string> PreApprovedTools => _preApprovedTools;

    // AC-1031: set once an interrupt the operator asked for went through, so the turn it ends is not drawn as a
    // failure. Cleared when that turn's TurnCompleted has been folded, or when a new turn starts.
    public bool InterruptRequested { get; set; }

    // Whether this host writes a transcript at all; the design-time and most test graphs have no store.
    public bool RecordsTranscript => _transcriptStore is not null;

    // AC-251: raised the moment the runtime exists, before its start is awaited — when the session's working life starts.
    public event Action<DateTimeOffset>? Started;

    // AC-1378: the one start both the desktop pane and the backend launcher run, in the order the pane always ran it.
    // Null when this host cannot launch; a launch that throws leaves the attached runtime in `Runtime`.
    public async Task<SessionLaunched?> StartAsync(SessionStart start)
    {
        if (start.Profile is { } profile)
        {
            StartLoginPoll(profile);
        }

        PreApprove(start.PreApprovedTools, start.PreApproveAllTools);
        var launchOptions = _WithPaneId(start.LaunchOptions, _paneId());
        if (!CanLaunch)
        {
            return null;
        }

        var runtime = Attach(start.Profile);
        Started?.Invoke(_time.GetLocalNow());
        await runtime.StartAsync(
            start.Profile, start.PermissionMode, start.Model, start.EnabledMcpServerNames, start.WorkingDirectory, start.Resume,
            launchOptions, start.ProjectId);
        _liveState.SetPermissionMode(start.PermissionMode);
        _RaiseLiveStateIfChanged();
        StartUsageCatchUp();
        return new SessionLaunched(runtime.IsRunning, runtime.ProcessId, runtime.Capabilities, runtime.LiveOptions);
    }

    // AC-13: the pane's own id rides along, which the provider's plugin turns into COCKPIT_PANE_ID for `set_status`.
    private static IReadOnlyDictionary<string, string> _WithPaneId(IReadOnlyDictionary<string, string>? launchOptions, string paneId)
    {
        var merged = launchOptions is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(launchOptions, StringComparer.OrdinalIgnoreCase);
        merged[WellKnownPluginSessionOptions.PaneId] = paneId;
        return merged;
    }

    // Creates the runtime and starts listening to it; the caller starts it.
    public ISessionRuntime Attach(SessionProfile? profile)
    {
        if (_manager is null)
        {
            throw new InvalidOperationException("This session host was built without a session manager.");
        }

        var runtime = _manager.Create(profile);
        runtime.EventAppended += _OnRuntimeEvent;
        Runtime = runtime;
        return runtime;
    }

    // Stops re-issuing the runtime's events, ahead of `StopAsync`, so the consumer can flush what it already has.
    public void StopListening()
    {
        if (Runtime is not null)
        {
            Runtime.EventAppended -= _OnRuntimeEvent;
        }
    }

    // Clears `Runtime` before its first await, so a caller reading readiness right after the call sees it gone.
    public async Task StopAsync()
    {
        if (Runtime is not { } runtime)
        {
            return;
        }

        Runtime = null;
        if (_manager is not null)
        {
            await _manager.StopAsync(runtime.Id);
        }
        else
        {
            await runtime.DisposeAsync();
        }
    }

    // One counter across hosts, so seq is unique backend-wide; within a host it rises because a runtime raises in order.
    private void _OnRuntimeEvent(SessionEvent evt)
    {
        EventAppended?.Invoke(new SessionHostEvent(SessionEventSequence.Next(), evt));
        _deliver(evt);
    }

    // AC-1438: where the events are folded. `post` runs an action on the consumer's thread; with `postAfterWindow`,
    // the events go there in batches a frame apart (AC-529, AC-1204), otherwise one post per event.
    public void PumpOn(Action<Action> post, Action<Action>? postAfterWindow = null)
    {
        if (postAfterWindow is null)
        {
            _deliver = evt => post(() => Pump(evt));
            _events = null;
            return;
        }

        var events = new SessionEventQueue(Pump, post, postAfterWindow);
        _events = events;
        _deliver = events.Enqueue;
    }

    // Whether events wait in a batch the consumer has not drained; see `FlushPumped`.
    public bool HasPumpedWork => _events?.HasWork == true;

    // Folds what the batch still holds, on the consumer's thread, ahead of a stop.
    public void FlushPumped() => _events?.Flush();

    // AC-1438: the one consumer step, on the consumer's thread: the fold, and the end of a completed turn, which sends
    // what was queued behind it. Public as the seam a test drives in place of a runtime.
    public void Pump(SessionEvent evt)
    {
        var fold = ApplyToTranscript(evt);
        Folded?.Invoke(fold);

        // A session error ends the turn without completing it: the queue stays, so a broken session is not cascaded.
        if (evt is TurnCompleted && !EndsAfterThisTurn)
        {
            CompleteTurn();
        }
    }

    public void CompleteTurn()
    {
        IsBusy = false;
        _DispatchNextQueued();
    }

    // Queues behind a turn in flight, unless the runtime takes input mid-turn (AC-739); otherwise sends now.
    public Task SubmitAsync(QueuedPrompt prompt, bool takesMidTurnInput = false)
    {
        if (IsBusy && !takesMidTurnInput)
        {
            Queue.Add(prompt);
            return Task.CompletedTask;
        }

        return DispatchAsync(prompt);
    }

    // For a send started where nothing can await it (a Retry click); disposal awaits it instead.
    public void DispatchInBackground(QueuedPrompt prompt) => _Track(DispatchAsync(prompt));

    // Sends one turn now. A send that throws is reported through `TurnFailedToStart`, never out of here.
    public async Task DispatchAsync(QueuedPrompt prompt)
    {
        if (Runtime is not { } runtime)
        {
            return;
        }

        // The consumer may set IsBusy itself inside these handlers, where its own bookkeeping wants it; setting it
        // again here is then a no-op, and a consumer that does not still gets a gate that reads busy. The operator
        // sending again is the one having been back to a session that asked for attention.
        _liveState.ClearNeedsAttention();

        // AC-1031: a stale flag from a Stop whose own turn end never arrived must not paint this turn's failure as one.
        InterruptRequested = false;
        TurnStarting?.Invoke(prompt);
        _transcript.EndReply();
        IsBusy = true;
        _RaiseLiveStateIfChanged();

        try
        {
            await _SendWithWaitingMessagesAsync(runtime, OutgoingText(prompt), prompt.Images, noteMail: true);
        }
        catch (Exception ex)
        {
            TurnFailedToStart?.Invoke(prompt, ex);
            IsBusy = false;
        }
    }

    // A turn the caller already marked busy and whose failure it decides about (a scheduled resume, AC-410).
    public Task SendPromptAsync(string prompt) => Runtime is { } runtime
        ? _SendWithWaitingMessagesAsync(runtime, prompt, images: null, noteMail: false)
        : Task.CompletedTask;

    // The one place a turn is handed to the runtime, so that turn-start delivery (AC-394) cannot be reached by one send
    // path and missed by another — `SessionHostSendPathTests` holds it to that.
    private async Task _SendWithWaitingMessagesAsync(
        ISessionRuntime runtime,
        string text,
        IReadOnlyList<ImageAttachment>? images,
        bool noteMail)
    {
        if (TurnsHeldBecause is { } held)
        {
            throw new InvalidOperationException(held);
        }

        // Only a runtime that is actually running can carry a turn, and "did not throw" is not enough to tell.
        var waiting = runtime.IsRunning ? _turnInboxDelivery?.TakeForTurn(_paneId()) : null;

        try
        {
            // A throw between the taking and the try would leave them held for the life of the pane — counted against
            // its inbox cap, invisible to read_inbox, and freed only when the session closes.
            var outgoing = waiting is null ? text : $"{waiting.Render()}\n\n{text}";
            await runtime.SendUserMessageAsync(outgoing, images);
        }
        catch
        {
            // The turn never left, so neither did the mail: put it back before the failure travels on, or the sender
            // was told it arrived while the recipient never saw it.
            if (waiting is not null)
            {
                _turnInboxDelivery?.ReturnUndelivered(waiting);
            }

            throw;
        }

        if (waiting is not null)
        {
            if (noteMail)
            {
                MailDelivered?.Invoke(waiting);
            }

            _turnInboxDelivery?.ConfirmDelivered(waiting);
        }
    }

    // Sends the next queued prompt (T8) once a turn frees the session. The dispatch's synchronous part marks the
    // session busy before its first await, so the status settles at once.
    private void _DispatchNextQueued()
    {
        if (Queue.Count == 0 || TurnsHeldBecause is not null)
        {
            return;
        }

        QueuedPrompt next;
        if (CombineQueued && Queue.Count > 1)
        {
            // AC-935: each prompt keeps its own prefix — one over the merged text would misattribute all but the first.
            var combinedText = string.Join(
                "\n\n",
                Queue.Select(OutgoingText).Where(text => !string.IsNullOrWhiteSpace(text)));
            var combinedImages = Queue.SelectMany(prompt => prompt.Images).ToList();
            Queue.Clear();
            next = new QueuedPrompt(combinedText, combinedImages);
        }
        else
        {
            next = Queue[0];
            Queue.RemoveAt(0);
        }

        _Track(DispatchAsync(next));
    }

    private void _Track(Task task)
    {
        _inFlight.RemoveAll(pending => pending.IsCompleted);
        _inFlight.Add(task);
    }

    // AC-1437: also folds what the pane shows besides its rows, raising each signal in the order
    // `SessionViewModel.Apply` acted on it: tool progress and a mid-turn start before the rows, a turn's end before
    // the status it leaves behind, so a consumer cleans up the turn before the session reads as done.
    public TranscriptFold ApplyToTranscript(SessionEvent evt)
    {
        if (evt is ToolUseRequested or ToolResult)
        {
            ToolProgressed?.Invoke();
        }

        // AC-1319: a CLI with its own input queue starts turns nobody sent here, so the agent's first top-level output
        // is the only sign one began.
        if (!IsBusy && (Capabilities ?? Runtime?.Capabilities) is { SupportsMidTurnInput: true } && evt.ParentToolUseId is null
            && evt is AssistantTextDelta or AssistantThinkingDelta or AssistantTextCompleted or ToolUseRequested or ToolResult)
        {
            IsBusy = true;
        }

        var fold = _transcript.Apply(evt);
        fold = fold with
        {
            IsReply = evt is AssistantTextDelta or AssistantTextCompleted && string.IsNullOrEmpty(evt.ParentToolUseId),
            AsksTheOperator = evt is Question || evt is PermissionRequested && fold.Row is { IsPendingPermission: true },
        };
        var (end, notice) = _liveState.Apply(evt, fold, InterruptRequested, _time.GetLocalNow());
        _RaiseProduced(evt, fold);
        if (notice is not null)
        {
            BackgroundTaskNotified?.Invoke(notice);
        }

        if (end is not null)
        {
            // AC-1031: consumed by the turn it ended, so a later genuine failure does not read as interrupted too.
            if (!end.BySessionError)
            {
                InterruptRequested = false;
            }

            TurnEnded?.Invoke(end);
            IsBusy = false;
        }

        _RaiseLiveStateIfChanged();
        return fold;
    }

    // AC-1324: the operator answered the last prompt, so the session runs on and stops flagging itself.
    public void ClearNeedsAttention()
    {
        _liveState.ClearNeedsAttention();
        _RaiseLiveStateIfChanged();
    }

    // A conversation that starts over in the same pane (AC-564).
    public void ResetLiveState()
    {
        _liveState.Reset();
        _RaiseLiveStateIfChanged();
    }

    private void _RaiseLiveStateIfChanged()
    {
        var liveState = LiveState;
        if (liveState == _raisedLiveState)
        {
            return;
        }

        _raisedLiveState = liveState;
        LiveStateChanged?.Invoke(liveState);
    }

    // AC-146: a sub-agent's own text or result is never the session's output, nor is an orphaned one's.
    private void _RaiseProduced(SessionEvent evt, TranscriptFold fold)
    {
        switch (evt)
        {
            case AssistantTextCompleted { ParentToolUseId: null or "" } completed when !string.IsNullOrEmpty(completed.Text):
                OutputTextProduced?.Invoke(completed.Text);
                break;

            case ToolResult { ParentToolUseId: null or "" } result when !fold.InSubAgentLane:
                // Tool output is where a shelled-out `gh pr create` prints its pull-request url (the PR watcher's channel).
                if (!string.IsNullOrEmpty(result.Content))
                {
                    OutputTextProduced?.Invoke(result.Content);
                }

                if (fold.Row is { Kind: ToolUseKind, ToolName: { Length: > 0 } toolName } row)
                {
                    ToolActivityProduced?.Invoke(new SessionToolCall(_paneId(), toolName, row.InputJson ?? "{}", result.Content, result.IsError));
                }

                break;
        }
    }

    public void RecordRow(TranscriptSnapshotEntry row) => _transcript.Record(row);

    // AC-1438: the rows as they stand, for a consumer that missed some of their upserts; read on the consumer's thread.
    public IReadOnlyList<TranscriptSnapshotEntry> Rows => _transcript.Rows;

    // A cleared context (AC-564) starts a new conversation in the same rows; the streaming state goes with it.
    public void ResetTranscriptStreaming() => _transcript.ResetStreaming();

    // AC-1090: the rows this pane recorded. Null when the log is there but could not be read, so the consumer can say
    // so rather than show a pane with no history.
    public Task<IReadOnlyList<TranscriptSnapshotEntry>?> LoadRecordedTranscriptAsync(CancellationToken cancellationToken = default) =>
        _transcriptStore?.TryLoadAsync(_paneId(), cancellationToken) ?? Task.FromResult<IReadOnlyList<TranscriptSnapshotEntry>?>([]);

    // The rows the consumer repainted, held so later changes version them; never published or written back.
    public void SeedTranscript(IReadOnlyList<TranscriptSnapshotEntry> rows) => _transcript.Seed(rows);

    // AC-947: a new conversation is a new log.
    public Task ArchiveRecordedTranscriptAsync(CancellationToken cancellationToken = default) =>
        _transcriptStore?.ArchiveAsync(_paneId(), cancellationToken) ?? Task.CompletedTask;

    // Not awaited, nor tracked for disposal: waiting on the store's debounce window (AC-1151) would hold a pane's close
    // for up to five seconds. It cannot fault: `SessionTranscriptLog._DebounceThenWriteAsync` catches the write, and
    // its one await outside that catch faults only on cancellation, which `CancellationToken.None` rules out.
    private void _OnRowChanged(int version, TranscriptSnapshotEntry row)
    {
        _ = _transcriptStore?.AppendAsync(_paneId(), row, CancellationToken.None);
        RowUpserted?.Invoke(new TranscriptRowUpsert(SessionEventSequence.Next(), version, row));
    }

    // AC-1031: marked only once the interrupt went through, so a failed one leaves the turn's failure drawn as one.
    public async Task InterruptAsync()
    {
        if (Runtime is { } runtime)
        {
            await runtime.InterruptAsync();
            InterruptRequested = true;
        }
    }

    // AC-409: recorded only once the switch took; recording a failed one would have a restart bring back a mode this
    // session never ran under.
    public async Task SetPermissionModeAsync(string mode)
    {
        if (Runtime is { } runtime)
        {
            await runtime.SetPermissionModeAsync(mode);
            _liveState.SetPermissionMode(mode);
            _RaiseLiveStateIfChanged();
            _ = _stateRecorder?.RecordPermissionModeChangedAsync(_paneId(), mode);
        }
    }

    public async Task SetModelAsync(string? model)
    {
        if (Runtime is { } runtime)
        {
            await runtime.SetModelAsync(model);
            _liveState.SetModel(model);
            _RaiseLiveStateIfChanged();
        }
    }

    public Task SetMaxThinkingTokensAsync(int maxThinkingTokens) =>
        Runtime?.SetMaxThinkingTokensAsync(maxThinkingTokens) ?? Task.CompletedTask;

    public Task SetLiveOptionAsync(string key, string value) => Runtime?.SetLiveOptionAsync(key, value) ?? Task.CompletedTask;

    public Task SetAutoApproveToolsAsync(bool autoApprove) => Runtime?.SetAutoApproveToolsAsync(autoApprove) ?? Task.CompletedTask;

    public Task CompactContextAsync() => Runtime?.CompactContextAsync() ?? Task.CompletedTask;

    public Task RespondToPermissionAsync(string toolUseId, bool allow, string? answersJson) =>
        Runtime?.RespondToPermissionAsync(toolUseId, allow, answersJson, CancellationToken.None) ?? Task.CompletedTask;

    public Task AllowPermissionAlwaysAsync(string toolUseId, string toolName, string inputJson, PermissionRuleScope scope) =>
        Runtime?.AllowPermissionAlwaysAsync(toolUseId, toolName, inputJson, scope) ?? Task.CompletedTask;

    // AC-215: the tools a self-driving run allows without asking, since it has no one to answer a prompt.
    public void PreApprove(IReadOnlyList<string>? tools, bool allTools)
    {
        _preApprovedTools = tools is { Count: > 0 }
            ? new HashSet<string>(tools, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        PreApprovesAllTools = allTools;
    }

    // AC-215: allows a pre-authorized tool on the wire; false when the operator has to be asked.
    public bool TryAutoAllow(PermissionRequested permission)
    {
        if (!(PreApprovesAllTools || _preApprovedTools.Contains(permission.ToolName)) || Runtime is not { } runtime)
        {
            return false;
        }

        _Track(runtime.RespondToPermissionAsync(permission.ToolUseId, allow: true));
        return true;
    }

    // AC-775: the runtime's own reading when it has one, shared with sessions on the same credential; otherwise what
    // such a session last published.
    public SessionStatusFeed? ReadUsageStatus(ProviderConfig? config)
    {
        var status = Runtime?.CurrentStatus;
        if (status is { HasAny: true })
        {
            _sharedUsageCache?.Set(config, status);
            return status;
        }

        return _sharedUsageCache?.TryGet(config);
    }

    // AC-713: checks the login now and then every minute. A second call restarts the minute on the same timer, since
    // clearing the context re-runs the start (AC-564).
    public void StartLoginPoll(SessionProfile profile)
    {
        if (_loginChecker is null)
        {
            return;
        }

        _loginProfile = profile;
        _loginPoll ??= _time.CreateTimer(_ => _Tick("login poll", _CheckLogin), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _CheckLogin();
        _loginPoll.Change(LoginPollInterval, LoginPollInterval);
    }

    private void _CheckLogin()
    {
        if (_loginChecker is not null && _loginProfile is { } profile)
        {
            LoginChecked?.Invoke(_loginChecker.IsLoggedIn(profile));
        }
    }

    // Started once; a re-read of the runtime's already-known status, with no CLI traffic of its own.
    public void StartUsageCatchUp() =>
        _usageCatchUp ??= _time.CreateTimer(_ => _Tick("usage catch-up", () => UsageCatchUpDue?.Invoke()), null, UsageCatchUpInterval, UsageCatchUpInterval);

    // False once the pane is closing. A tick posted just before that still arrives, and asks this before it acts.
    public bool IsPolling { get; private set; } = true;

    // The pane is closing: nothing is left to poll for.
    public void StopPolling()
    {
        IsPolling = false;
        _loginPoll?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _usageCatchUp?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    // AC-598: one tick after `delay`; the consumer re-arms it from its handler. A timer per arm, with the arm taken
    // when it is set: read when it fires instead, a tick racing a restart would carry the restart's arm as its own.
    public void RestartSignOfLife(TimeSpan delay)
    {
        var arm = Interlocked.Increment(ref _signOfLifeArm);
        _signOfLife?.Dispose();
        _signOfLife = _time.CreateTimer(
            _ => _Tick("sign of life", () => SignOfLifeDue?.Invoke(arm)), null, delay, Timeout.InfiniteTimeSpan);
    }

    public void StopSignOfLife()
    {
        Interlocked.Increment(ref _signOfLifeArm);
        _signOfLife?.Dispose();
        _signOfLife = null;
    }

    // A tick reaches its consumer through a post, and a restart or stop can land in between; a stale arm is dropped.
    public bool IsCurrentSignOfLife(int arm) => arm == Volatile.Read(ref _signOfLifeArm);

    // A throw out of a pool-thread timer callback takes the process down; logged and swallowed, the timer keeps
    // ticking, as the UI thread's net did for the DispatcherTimers these replace.
    private void _Tick(string timer, Action tick)
    {
        try
        {
            tick();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "The session's {Timer} tick failed; the timer keeps running.", timer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        IsPolling = false;
        StopSignOfLife();
        foreach (var timer in new[] { _loginPoll, _usageCatchUp, _signOfLife })
        {
            if (timer is not null)
            {
                await timer.DisposeAsync();
            }
        }

        try
        {
            await Task.WhenAll(_inFlight).WaitAsync(InFlightDrainBudget, _time);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "A send or permission answer of a closing session did not settle cleanly.");
        }
    }
}
