using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// AC-1378: one SDK session without a view model and the consumer its host asks for, doing `SessionViewModel.Apply`'s
// duties; one lock stands in for the UI thread so the fold, the turn gate and the reads never interleave.
// AC-1379: also the assistant's session without the app, as `AssistantSessionHost` drives it.
public sealed class SessionHostHandle : ISessionHandle, IAssistantSession
{
    // `TranscriptEntryKind.UserText` and `.Divider` as the transcript store spells them, like `SessionTranscriptBuilder`'s kinds.
    private const string UserText = "UserText";
    private const string Divider = "Divider";

    private readonly Lock _gate = new();
    private readonly SessionHost<QueuedPrompt> _host;
    private readonly bool _nameIsChosen;
    private readonly List<TranscriptSnapshotEntry> _rows = [];
    private string _title;
    private string _statusline = string.Empty;
    private string? _worktreeBranch;
    private readonly List<ImageAttachment> _pendingImages = [];
    private string _failureReason = "Not started.";
    private bool _wasWaitingOnOperator;
    private bool _stateChangePending;

    public SessionHostHandle(
        string paneId,
        string title,
        bool nameIsChosen,
        string workspaceId,
        string? workingDirectory,
        string? profileLabel,
        SessionHost<QueuedPrompt> host)
    {
        PaneId = paneId;
        _title = title;
        _nameIsChosen = nameIsChosen;
        WorkspaceId = workspaceId;
        WorkingDirectory = workingDirectory;
        ActiveProfileLabel = profileLabel;
        _host = host;
        host.RowUpserted += _OnRowUpserted;
        host.EventAppended += _OnEventAppended;
        host.TurnStarting += _OnTurnStarting;
        host.BusyChanged += _NoteStateChanged;
    }

    public string PaneId { get; }

    public string Title
    {
        get
        {
            lock (_gate)
            {
                return _title;
            }
        }
    }

    public string WorkspaceId { get; }

    // Only ever started onto a Sessions desk it names, so the desk it counts as sitting on is its own.
    public string? PlacedWorkspaceId => WorkspaceId;

    public string? WorkingDirectory { get; }

    public string? WorktreeBranch
    {
        get
        {
            lock (_gate)
            {
                return _worktreeBranch;
            }
        }
    }

    public string? ActiveProfileLabel { get; }

    public bool IsTerminal => false;

    // AC-294: an SDK session's record is in memory and always there, the same as `SessionPanelHandle`'s.
    public bool HasReadableTranscript => true;

    public bool IsEmbedded => false;

    public SessionStatus SessionStatus
    {
        get
        {
            lock (_gate)
            {
                return _host.IsBusy ? SessionStatus.Busy : SessionStatus.Idle;
            }
        }
    }

    public string Statusline
    {
        get
        {
            lock (_gate)
            {
                return _statusline;
            }
        }
    }

    // `SessionViewModel.CanTakeAPrompt`'s rule: a running runtime, and no hold on new turns.
    public bool CanTakeAPrompt
    {
        get
        {
            lock (_gate)
            {
                return _host.Runtime is { IsRunning: true } && _host.TurnsHeldBecause is null;
            }
        }
    }

    // The host is built without turn-start inbox delivery, and a prompt is never held: the handle exists once started.
    public bool DeliversInboxAtTurnStart => false;

    public bool HasPromptWaitingToBeDelivered => false;

    public Task<bool> HasOutstandingBackgroundShellsAsync() => Task.FromResult(false);

    // A consent banner is a view's; the permission rows themselves are answered through the members below.
    public bool HasPendingConsent => false;

    // The process meter is the desktop's; nothing measures a headless session's tree yet.
    public int ProcessCount => 0;

    public double ProcessCpuPercent => 0;

    public long ProcessMemoryBytes => 0;

    public int AbandonedProcessCount => 0;

    public Task<SessionTranscriptSlice> ReadTranscriptAsync(int count)
    {
        lock (_gate)
        {
            var skip = Math.Max(0, _rows.Count - count);
            return Task.FromResult(new SessionTranscriptSlice(
                [.. _rows.Skip(skip).Select(row => new SessionTranscriptEntry(row.Kind, row.Text, row.ResultText))],
                _rows.Count));
        }
    }

    // True once the prompt went out or waits behind the turn in flight; false when it was refused or its send failed.
    public async Task<bool> SendPromptAsync(string prompt)
    {
        var queued = new QueuedPrompt(prompt, []);
        var failed = false;
        void OnFailed(QueuedPrompt failedPrompt, Exception exception) => failed |= ReferenceEquals(failedPrompt, queued);

        _host.TurnFailedToStart += OnFailed;
        try
        {
            Task sending;
            lock (_gate)
            {
                if (_host.Runtime is not { IsRunning: true } || _host.TurnsHeldBecause is not null)
                {
                    return false;
                }

                sending = _host.SubmitAsync(queued);
            }

            await sending.ConfigureAwait(false);
            return !failed;
        }
        finally
        {
            _host.TurnFailedToStart -= OnFailed;
        }
    }

    // Nothing is ever held here: false says the prompt was not taken, and nothing will deliver it later.
    public async Task<bool?> SubmitPromptWhenReadyAsync(string prompt) => await SendPromptAsync(prompt).ConfigureAwait(false);

    public Task SetWorktreeBranchAsync(string? branch)
    {
        lock (_gate)
        {
            _worktreeBranch = branch;
        }

        return Task.CompletedTask;
    }

    // ponytail: top-level rows only; a sub-agent's permission row needs its parent re-recorded, add that with a caller.
    public async Task<bool> RespondToPermissionByIdAsync(string toolUseId, bool allow)
    {
        Task answering;
        lock (_gate)
        {
            if (_host.Runtime is not { } runtime
                || _rows.Find(row => row.IsPendingPermission && string.Equals(row.ToolUseId, toolUseId, StringComparison.Ordinal)) is not { } row)
            {
                return false;
            }

            _host.RecordRow(row with { IsPendingPermission = false, PermissionDecision = allow ? "Allowed" : "Denied" });
            answering = runtime.RespondToPermissionAsync(toolUseId, allow);
        }

        _RaisePendingStateChange();
        await answering.ConfigureAwait(false);
        return true;
    }

    // A verify render is an image a view attaches; a headless session has no such turn to hand it.
    public Task<bool> FeedVerifyResultAsync(string caption, byte[] screenshotPng) => Task.FromResult(false);

    public Task<IReadOnlyList<SessionPendingPermission>> ReadPendingPermissionsAsync()
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<SessionPendingPermission>>(
            [
                .. _rows.Where(row => row.IsPendingPermission).Select(row =>
                    new SessionPendingPermission(row.ToolUseId ?? "", row.ToolName ?? "", row.InputJson ?? "{}", row.Timestamp)),
            ]);
        }
    }

    public Task<bool> SetStatuslineAsync(string statusline)
    {
        lock (_gate)
        {
            _statusline = statusline ?? string.Empty;
        }

        return Task.FromResult(true);
    }

    // AC-310: a name the operator chose stays standing.
    public Task<bool> SuggestNameAsync(string name)
    {
        if (_nameIsChosen || string.IsNullOrWhiteSpace(name))
        {
            return Task.FromResult(false);
        }

        lock (_gate)
        {
            _title = name.Trim();
        }

        return Task.FromResult(true);
    }

    public Task<SessionWakeState> ReadWakeStateAsync()
    {
        lock (_gate)
        {
            return Task.FromResult(new SessionWakeState(
                HasPendingConsent, _host.IsBusy ? SessionStatus.Busy : SessionStatus.Idle, CanTakeAPrompt));
        }
    }

    // ── the assistant's session (AC-1379) ──

    public bool IsSessionReady
    {
        get
        {
            lock (_gate)
            {
                return _host.Runtime is { IsRunning: true };
            }
        }
    }

    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _host.IsBusy;
            }
        }
    }

    // A consent card is a view's, so only the permission rows can wait here.
    public bool IsWaitingOnOperator
    {
        get
        {
            lock (_gate)
            {
                return _rows.Exists(row => row.IsPendingPermission);
            }
        }
    }

    // ponytail: no usage feed reaches a headless session yet, so its fill is unknown and it never hands over for it.
    public double? ContextUsedPercent => null;

    public bool SupportsContextCompaction => _host.Runtime?.Capabilities is { SupportsContextCompaction: true };

    public string? TurnsHeldBecause
    {
        get
        {
            lock (_gate)
            {
                return _host.TurnsHeldBecause;
            }
        }
        set
        {
            lock (_gate)
            {
                _host.TurnsHeldBecause = value;
            }
        }
    }

    // Kept for what a later reader of this session asks; a headless row has no presentation to switch.
    public ReadingLevel ReadingLevel { get; set; } = ReadingLevel.Developer;

    public string FailureReason
    {
        get
        {
            lock (_gate)
            {
                return _failureReason;
            }
        }
    }

    public bool CanPasteImages => _host.Runtime?.Capabilities is { SupportsVision: true };

    public bool HasPendingAttachments
    {
        get
        {
            lock (_gate)
            {
                return _pendingImages.Count > 0;
            }
        }
    }

    public event EventHandler<bool>? StateChanged;

    public event Action<TranscriptRowUpsert>? RowUpserted
    {
        add => _host.RowUpserted += value;
        remove => _host.RowUpserted -= value;
    }

    public IReadOnlyList<TranscriptSnapshotEntry> Rows
    {
        get
        {
            lock (_gate)
            {
                return [.. _rows];
            }
        }
    }

    // `SessionViewModel.PrepareRecordedTranscriptAsync`'s rule: a resumed conversation repaints its log, a new one rolls it.
    public async Task PrepareRecordedTranscriptAsync(SessionResume resume, CancellationToken cancellationToken = default)
    {
        if (resume.Mode != SessionResumeMode.BySessionId)
        {
            await _host.ArchiveRecordedTranscriptAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // A permission prompt in the log was answered or abandoned by the run that wrote it; like the desktop's restore
        // (`TranscriptSnapshot.Restore`), it does not come back as one still waiting.
        if (await _host.LoadRecordedTranscriptAsync(cancellationToken).ConfigureAwait(false) is { } recorded)
        {
            IReadOnlyList<TranscriptSnapshotEntry> settled = [.. recorded.Select(row => row with { IsPendingPermission = false })];
            lock (_gate)
            {
                _host.SeedTranscript(settled);
                _rows.AddRange(settled);
            }
        }
    }

    // The permission-mode floor the desktop's assistant starts on; the profile's own mode rides the launch options.
    public async Task StartAsync(AssistantLaunch launch)
    {
        try
        {
            var runtime = await _host.StartAsync(new SessionStart(
                launch.Profile, SessionPermissionModes.Default, Model: null, launch.EnabledMcpServerNames,
                launch.WorkingDirectory, launch.Resume, launch.LaunchOptions, ProjectId: null)).ConfigureAwait(false);
            if (runtime is not { IsRunning: true })
            {
                _SetFailure("The provider returned without a running session.");
            }
        }
        catch (Exception exception)
        {
            _SetFailure(exception.Message);
        }
    }

    public void AddPastedImage(byte[] pngBytes)
    {
        lock (_gate)
        {
            _pendingImages.Add(ImageAttachment.FromBytes(pngBytes, "image/png"));
        }
    }

    public void SubmitComposer() => InjectAndSubmit(string.Empty);

    // Queued behind a turn in flight or a hold, like the desktop's send; otherwise sent now and awaited on disposal.
    public void InjectAndSubmit(string text)
    {
        lock (_gate)
        {
            var prompt = new QueuedPrompt(text, [.. _pendingImages]);
            _pendingImages.Clear();
            if (_host.IsBusy || _host.TurnsHeldBecause is not null)
            {
                _host.Queue.Add(prompt);
            }
            else
            {
                _host.DispatchInBackground(prompt);
            }
        }

        _RaisePendingStateChange();
    }

    public async Task<bool> CompactContextAsync()
    {
        if (_host.Runtime is not { IsRunning: true } runtime || TurnsHeldBecause is not null || !SupportsContextCompaction)
        {
            return false;
        }

        try
        {
            await runtime.CompactContextAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void AddDivider(string text)
    {
        lock (_gate)
        {
            _host.RecordRow(new TranscriptSnapshotEntry(
                Guid.NewGuid().ToString("n"), Divider, text, ToolName: null, InputJson: null,
                ToolUseId: null, ResultText: null, IsResultError: false, DateTimeOffset.Now));
        }

        _RaisePendingStateChange();
    }

    // Never raised inside the lock: the assistant's host reacts by starting over, whose teardown waits on the very
    // thread still inside it. Noted there, raised once the outermost section has let go.
    private void _NoteStateChanged()
    {
        if (_gate.IsHeldByCurrentThread)
        {
            _stateChangePending = true;
            return;
        }

        StateChanged?.Invoke(this, false);
    }

    private void _RaisePendingStateChange()
    {
        if (_gate.IsHeldByCurrentThread || !_stateChangePending)
        {
            return;
        }

        _stateChangePending = false;
        StateChanged?.Invoke(this, false);
    }

    // A consent card is a view's; a headless session never has one open.
    public void DenyPendingConsent()
    {
    }

    // Speech is the desktop's: nothing plays a headless session's replies.
    public void ApplySpeech(bool speakReplies)
    {
    }

    public void SpeakReplies(bool speak)
    {
    }

    private void _SetFailure(string reason)
    {
        lock (_gate)
        {
            _failureReason = reason;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _host.StopListening();
        await _host.StopAsync().ConfigureAwait(false);
        await _host.DisposeAsync().ConfigureAwait(false);
    }

    // Stream order is the runtime's, and a session error ends the turn it broke, as it does in the view model.
    private void _OnEventAppended(SessionHostEvent hostEvent)
    {
        lock (_gate)
        {
            _host.ApplyToTranscript(hostEvent.Event);
            if (hostEvent.Event is TurnCompleted)
            {
                _host.CompleteTurn();
            }
            else if (hostEvent.Event is SessionError)
            {
                _host.IsBusy = false;
            }
        }

        _RaisePendingStateChange();
    }

    // The operator's own row, which the desktop pane forms itself (`SessionViewModel._OnTurnStarting`). A turn starts
    // from a send or from the end of the previous turn, both under the lock.
    private void _OnTurnStarting(QueuedPrompt prompt) =>
        _host.RecordRow(new TranscriptSnapshotEntry(
            Guid.NewGuid().ToString("n"), UserText, prompt.Text, ToolName: null, InputJson: null,
            ToolUseId: null, ResultText: null, IsResultError: false, DateTimeOffset.Now));

    // Raised inside the fold or a recorded row, so under the same lock; a row keeps the place it first took.
    private void _OnRowUpserted(TranscriptRowUpsert upsert)
    {
        var index = _rows.FindIndex(row => string.Equals(row.Id, upsert.Row.Id, StringComparison.Ordinal));
        if (index < 0)
        {
            _rows.Add(upsert.Row);
        }
        else
        {
            _rows[index] = upsert.Row;
        }

        var waiting = _rows.Exists(row => row.IsPendingPermission);
        if (waiting != _wasWaitingOnOperator)
        {
            _wasWaitingOnOperator = waiting;
            _NoteStateChanged();
        }
    }
}
