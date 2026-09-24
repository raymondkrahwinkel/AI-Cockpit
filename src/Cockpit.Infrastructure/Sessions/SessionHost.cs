using System.Collections.ObjectModel;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
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
    /// The consumer calls CompleteTurn when it applies a TurnCompleted, in stream order; the host never ends a turn on its own.
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
    /// The consumer calls ApplyToTranscript for every event the host re-issues, in stream order; the host never folds an event on its own.
    /// It forms or updates the rows that event touches on the consumer's thread, and returns the row it landed on.
    /// </summary>
    TranscriptFold ApplyToTranscript(SessionEvent evt);

    /// <summary>
    /// Records a row the consumer formed or changed itself, so the host's rows and the store stay one transcript.
    /// </summary>
    void RecordRow(TranscriptSnapshotEntry row);
}

// A prompt waiting for the turn in flight to end (T8). A consumer that sends more than the text it shows, such as a
// reply prefix (AC-935), overrides `OutgoingText`; it is read when the prompt leaves, not when it was queued.
public class QueuedPrompt(string text, IReadOnlyList<ImageAttachment> images)
{
    public string Text { get; } = text;

    public IReadOnlyList<ImageAttachment> Images { get; } = images;

    public virtual string OutgoingText => Text;
}

// One runtime event as the host re-issues it, numbered on one counter for the whole backend so a stream can resume
// from a `Last-Event-ID` without renumbering (F5).
public readonly record struct SessionHostEvent(long Seq, SessionEvent Event);

// Outside the generic host on purpose: a static field on `SessionHost<TPrompt>` would be one counter per prompt type.
internal static class SessionEventSequence
{
    private static long _last;

    public static long Next() => Interlocked.Increment(ref _last);
}

// AC-1376 (F1.4): one SDK session's backend half, which `SessionViewModel` used to be: the runtime, the turn gate with
// its queue, the funnel every turn leaves through, and the three clocks. No dispatcher: members run on the consumer's
// thread and awaits resume there (no ConfigureAwait(false)); only the timer events arrive on the thread pool.
public sealed class SessionHost<TPrompt> : ISessionTurnGate, ISessionTranscript, IAsyncDisposable
    where TPrompt : QueuedPrompt
{
    // Same as `ClaudeLoginStatus.MaxAge`: a tick mostly re-reads a cache another poll already refreshed (AC-713).
    public static readonly TimeSpan LoginPollInterval = TimeSpan.FromMinutes(1);

    // AC-761 F3: catches a usage reply that missed its turn's publish grace, for a session that has since gone idle.
    public static readonly TimeSpan UsageCatchUpInterval = TimeSpan.FromSeconds(30);

    // Long enough for a send the stopped runtime is failing to settle, short enough that a hung one cannot hold a close.
    private static readonly TimeSpan InFlightDrainBudget = TimeSpan.FromSeconds(5);

    private readonly Func<string> _paneId;
    private readonly ISessionManager? _manager;
    private readonly TimeProvider _time;
    private readonly IAgentTurnInboxDelivery? _turnInboxDelivery;
    private readonly IProfileLoginChecker? _loginChecker;
    private readonly ISharedUsageCache? _sharedUsageCache;
    private readonly ILogger? _logger;

    // AC-1090: Cockpit's own copy of the conversation, written from here so a session without a view has one too.
    private readonly ISessionTranscriptStore? _transcriptStore;
    private readonly SessionTranscriptBuilder _transcript;

    // The sends and wire answers started where nothing can await them (a hold lifting, a turn ending), kept so the
    // host's own disposal does.
    private readonly List<Task> _inFlight = [];

    private bool _isBusy;
    private string? _turnsHeldBecause;
    private IReadOnlySet<string> _preApprovedTools = new HashSet<string>(StringComparer.Ordinal);

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
        ISessionTranscriptStore? transcriptStore = null)
    {
        _paneId = paneId;
        _manager = manager;
        _time = time;
        _turnInboxDelivery = turnInboxDelivery;
        _loginChecker = loginChecker;
        _sharedUsageCache = sharedUsageCache;
        _logger = logger;
        _transcriptStore = transcriptStore;
        _transcript = new SessionTranscriptBuilder(time, TryAutoAllow, () => InterruptRequested, _OnRowChanged);
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

    public ISessionRuntime? Runtime { get; private set; }

    // Whether this host can launch at all; the design-time graph builds one without a manager.
    public bool CanLaunch => _manager is not null;

    // Mutated on the consumer's thread only, which for the desktop is the UI thread that binds it.
    public ObservableCollection<TPrompt> Queue { get; } = [];

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
    // failure. The consumer clears it when that turn's TurnCompleted has been applied, or when a new turn starts.
    public bool InterruptRequested { get; set; }

    // Whether this host writes a transcript at all; the design-time and most test graphs have no store.
    public bool RecordsTranscript => _transcriptStore is not null;

    // AC-251: the session's working life starts when its runtime exists, not when the launch it waits on returns.
    public DateTimeOffset? StartedAt { get; private set; }

    // AC-1378: the one start both the desktop pane and the backend launcher run, in the order the pane always ran it.
    // Null when this host cannot launch; a launch that throws leaves the attached runtime in `Runtime`.
    public async Task<ISessionRuntime?> StartAsync(SessionStart start)
    {
        if (start.Profile is { } profile)
        {
            StartLoginPoll(profile);
        }

        PreApprove(start.PreApprovedTools, start.PreApproveAllTools);
        var launchOptions = SessionStart.WithPaneId(start.LaunchOptions, _paneId());
        if (!CanLaunch)
        {
            return null;
        }

        var runtime = Attach(start.Profile);
        StartedAt = _time.GetLocalNow();
        await runtime.StartAsync(
            start.Profile, start.PermissionMode, start.Model, start.EnabledMcpServerNames, start.WorkingDirectory, start.Resume,
            launchOptions, start.ProjectId);
        StartUsageCatchUp();
        return runtime;
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
    private void _OnRuntimeEvent(SessionEvent evt) =>
        EventAppended?.Invoke(new SessionHostEvent(SessionEventSequence.Next(), evt));

    public void CompleteTurn()
    {
        IsBusy = false;
        _DispatchNextQueued();
    }

    // Queues behind a turn in flight, unless the runtime takes input mid-turn (AC-739); otherwise sends now.
    public Task SubmitAsync(TPrompt prompt, bool takesMidTurnInput = false)
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
        // again here is then a no-op, and a consumer that does not still gets a gate that reads busy.
        TurnStarting?.Invoke(prompt);
        _transcript.EndReply();
        IsBusy = true;

        try
        {
            await _SendWithWaitingMessagesAsync(runtime, prompt.OutgoingText, prompt.Images, noteMail: true);
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
                Queue.Select(prompt => prompt.OutgoingText).Where(text => !string.IsNullOrWhiteSpace(text)));
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

    public TranscriptFold ApplyToTranscript(SessionEvent evt) => _transcript.Apply(evt);

    public void RecordRow(TranscriptSnapshotEntry row) => _transcript.Record(row);

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
