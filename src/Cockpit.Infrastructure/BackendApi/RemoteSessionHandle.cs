using System.Collections.Specialized;
using System.Net;
using System.Text.Json;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1388: one session on a backend reached over `/api/v1`. Its facts are the session list's as `RemoteBackend` last
// read it; its rows the transcript route's snapshot plus every later row the stream carries, each exactly once. A
// member the API has no route for refuses; one that only means something on the backend's machine reads empty.
public sealed class RemoteSessionHandle : ISessionHandle, ISessionControl
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly BackendApiClient _client;
    private readonly bool _isAssistant;
    private readonly Lock _gate = new();
    private readonly List<TranscriptSnapshotEntry> _rows = [];
    private readonly List<QueuedPrompt> _queue = [];
    private RemoteSessionRow _facts;
    private SessionLiveState? _liveState;
    private Action<Action> _post = action => action();
    private CancellationTokenSource? _signOfLife;
    private long _snapshotSeq;
    private int _signOfLifeArm;
    private bool _loaded;
    private bool _isAttached = true;
    private bool _isBusy;
    private bool _isListening = true;
    private bool _isPolling = true;
    private SessionCapabilities? _capabilities;
    private SessionStatusFeed? _usageStatus;

    internal RemoteSessionHandle(BackendApiClient client, string paneId, bool isAssistant)
    {
        _client = client;
        _isAssistant = isAssistant;
        _facts = new RemoteSessionRow(paneId, paneId);
    }

    public event Action<TranscriptRowUpsert>? RowUpserted;

    public event Action<SessionLiveState>? LiveStateChanged;

    public event Action<SessionToolCall>? ToolActivityProduced;

    public event Action? ToolProgressed;

    public event NotifyCollectionChangedEventHandler? QueueChanged;

    public event Action? BusyChanged;

    public event Action? UsageCatchUpDue;

    public event Action<SessionTurnEnd>? TurnEnded;

    public event Action<QueuedPrompt>? TurnStarting;

    public event Action<QueuedPrompt, Exception>? TurnFailedToStart;

    public event Action<int>? SignOfLifeDue;

    event Action<DateTimeOffset>? ISessionControl.Started { add { } remove { } }

    event Action<AgentInboxTurnNotice>? ISessionControl.MailDelivered { add { } remove { } }

    event Action<bool>? ISessionControl.LoginChecked { add { } remove { } }

    event Action<TranscriptFold>? ISessionControl.Folded { add { } remove { } }

    event Action<SessionBackgroundTaskNotice>? ISessionControl.BackgroundTaskNotified { add { } remove { } }

    event Action<string>? ISessionControl.OutputTextProduced { add { } remove { } }

    public string PaneId => _Facts.PaneId;

    public ISessionControl? Control => _isAssistant ? null : this;

    public string Title => _Facts.Name;

    public string WorkspaceId => RemoteBackend.NodeDeskId;

    public string? PlacedWorkspaceId => RemoteBackend.NodeDeskId;

    public string? WorkingDirectory => null;

    public string? WorktreeBranch => null;

    public string? ActiveProfileLabel => _Facts.Profile;

    public string? ProjectId => _Facts.ProjectId;

    public bool IsTerminal => false;

    public bool IsEmbedded => false;

    // The stream's live state once one arrived, the session list's word for it before.
    public SessionStatus SessionStatus
    {
        get
        {
            lock (_gate)
            {
                return _liveState?.Status ?? (Enum.TryParse<SessionStatus>(_facts.Status, ignoreCase: true, out var status) ? status : SessionStatus.Idle);
            }
        }
    }

    public string Statusline => _Facts.Statusline ?? string.Empty;

    // The prompt route holds a prompt until the session can take it, so none is refused for being early.
    public bool CanTakeAPrompt => true;

    public bool DeliversInboxAtTurnStart => false;

    public bool HasPromptWaitingToBeDelivered => false;

    public bool HasPendingConsent => false;

    public int ProcessCount => 0;

    public double ProcessCpuPercent => 0;

    public long ProcessMemoryBytes => 0;

    public int AbandonedProcessCount => 0;

    public bool HasReadableTranscript => true;

    public SessionLiveState LiveState
    {
        get
        {
            lock (_gate)
            {
                return _liveState ?? SessionLiveState.None;
            }
        }
    }

    // The snapshot the handle started from and every row the stream carried after it.
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

    public bool CanLaunch => false;

    public bool IsAttached
    {
        get
        {
            lock (_gate)
            {
                return _isAttached;
            }
        }
    }

    public bool IsRunning => IsAttached;

    public bool HasPumpedWork => false;

    public IReadOnlyCollection<string> PreApprovedTools => [];

    public bool PreApprovesAllTools => false;

    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _isBusy;
            }
        }

        set
        {
            var changed = false;
            lock (_gate)
            {
                if (_isBusy != value)
                {
                    _isBusy = value;
                    changed = true;
                }
            }

            if (changed)
            {
                _Post(() => BusyChanged?.Invoke());
            }
        }
    }

    public string? TurnsHeldBecause { get; set; }

    public bool EndsAfterThisTurn { get; set; }

    public bool CombineQueued { get; set; }

    public SessionCapabilities? Capabilities
    {
        get
        {
            lock (_gate)
            {
                return _capabilities;
            }
        }

        set
        {
            lock (_gate)
            {
                _capabilities = value;
            }
        }
    }

    public Func<QueuedPrompt, string> OutgoingText { get; set; } = prompt => prompt.Text;

    public IReadOnlyList<QueuedPrompt> Queue
    {
        get
        {
            lock (_gate)
            {
                return [.. _queue];
            }
        }
    }

    public bool RecordsTranscript => false;

    public bool IsPolling
    {
        get
        {
            lock (_gate)
            {
                return _isPolling;
            }
        }
    }

    private RemoteSessionRow _Facts
    {
        get
        {
            lock (_gate)
            {
                return _facts;
            }
        }
    }

    internal RemoteSessionRow Facts => _Facts;

    private string _Path => _isAssistant ? "api/v1/assistant" : $"api/v1/sessions/{Uri.EscapeDataString(PaneId)}";

    public Task<SessionLaunched?> StartAsync(SessionStart start) =>
        throw new NotSupportedException("A remote handle is attached by its backend and cannot start itself.");

    public void StopListening()
    {
        lock (_gate)
        {
            _isListening = false;
        }
    }

    public async Task StopAsync()
    {
        lock (_gate)
        {
            _isAttached = false;
        }

        await _client.SendAsync<JsonElement>(HttpMethod.Delete, _Path, null).ConfigureAwait(false);
    }

    public void PumpOn(Action<Action> post, Action<Action>? postAfterWindow = null)
    {
        ArgumentNullException.ThrowIfNull(post);
        lock (_gate)
        {
            _post = postAfterWindow ?? post;
        }
    }

    public void FlushPumped()
    {
    }

    public Task InterruptAsync() =>
        _client.SendAsync<JsonElement>(HttpMethod.Post, $"{_Path}/interrupt", null);

    public Task SetPermissionModeAsync(string mode) =>
        _client.SendAsync<JsonElement>(HttpMethod.Post, $"{_Path}/permission-mode", new { mode });

    public Task SetModelAsync(string? model) =>
        _client.SendAsync<JsonElement>(HttpMethod.Post, $"{_Path}/model", new { model });

    public Task SetMaxThinkingTokensAsync(int maxThinkingTokens) =>
        _Unsupported("The API does not expose a remote thinking-budget switch.");

    public Task SetLiveOptionAsync(string key, string value) =>
        _Unsupported("The API does not expose provider-specific live options.");

    public Task SetAutoApproveToolsAsync(bool autoApprove) =>
        _Unsupported("The API does not expose remote auto-approval.");

    public Task CompactContextAsync() =>
        _Unsupported("The API does not expose remote context compaction.");

    public async Task RespondToPermissionAsync(string toolUseId, bool allow, string? answersJson)
    {
        if (!string.IsNullOrWhiteSpace(answersJson))
        {
            throw new NotSupportedException("The API does not expose structured permission answers.");
        }

        await RespondToPermissionByIdAsync(toolUseId, allow).ConfigureAwait(false);
    }

    public Task AllowPermissionAlwaysAsync(string toolUseId, string toolName, string inputJson, PermissionRuleScope scope) =>
        _Unsupported("The API does not expose persistent permission rules.");

    public void ClearNeedsAttention() =>
        throw new NotSupportedException("The backend owns a remote session's attention state.");

    public void Enqueue(QueuedPrompt prompt)
    {
        _SetLocalQueue([.. Queue, prompt]);
        _ = _SendQueueCommandAndReportAsync("enqueue", prompt);
    }

    public bool Withdraw(QueuedPrompt prompt)
    {
        var queue = Queue;
        var existing = queue.FirstOrDefault(item => string.Equals(item.WireId, prompt.WireId, StringComparison.Ordinal));
        if (existing is null)
        {
            return false;
        }

        _SetLocalQueue([.. queue.Where(item => !string.Equals(item.WireId, prompt.WireId, StringComparison.Ordinal))]);
        _ = _SendQueueCommandAndReportAsync("withdraw", existing);
        return true;
    }

    public void ClearQueue()
    {
        _SetLocalQueue([]);
        _ = _SendQueueCommandAndReportAsync("clear", null);
    }

    public async Task SubmitAsync(QueuedPrompt prompt, bool takesMidTurnInput = false)
    {
        TurnStarting?.Invoke(prompt);
        try
        {
            await _SendQueueCommandAsync("submit", prompt, takesMidTurnInput).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            TurnFailedToStart?.Invoke(prompt, exception);
        }
    }

    public void DispatchInBackground(QueuedPrompt prompt) =>
        _ = _DispatchAndReportAsync(prompt);

    async Task ISessionControl.SendPromptAsync(string prompt)
    {
        await _client.SendAsync<JsonElement>(HttpMethod.Post, $"{_Path}/prompt", new { text = prompt }).ConfigureAwait(false);
    }

    public void ResetLiveState()
    {
        lock (_gate)
        {
            _liveState = SessionLiveState.None;
        }

        _Post(() => LiveStateChanged?.Invoke(SessionLiveState.None));
    }

    public SessionStatusFeed? ReadUsageStatus(ProviderConfig? config)
    {
        lock (_gate)
        {
            return _usageStatus;
        }
    }

    public void RecordRow(TranscriptSnapshotEntry row) =>
        throw new NotSupportedException("A remote transcript is recorded by its backend.");

    public void ResetTranscriptStreaming() =>
        throw new NotSupportedException("A remote transcript stream is reset by its backend.");

    public Task<IReadOnlyList<TranscriptSnapshotEntry>?> LoadRecordedTranscriptAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A remote transcript is read through the transcript route.");

    public void SeedTranscript(IReadOnlyList<TranscriptSnapshotEntry> rows) =>
        throw new NotSupportedException("A remote transcript is seeded by its backend snapshot.");

    public Task ArchiveRecordedTranscriptAsync(CancellationToken cancellationToken = default) =>
        _Unsupported("A remote transcript is archived by its backend.");

    public void StopPolling()
    {
        CancellationTokenSource? signOfLife;
        lock (_gate)
        {
            _isPolling = false;
            signOfLife = _signOfLife;
            _signOfLife = null;
        }

        signOfLife?.Cancel();
        signOfLife?.Dispose();
    }

    public void RestartSignOfLife(TimeSpan delay)
    {
        CancellationTokenSource current;
        int arm;
        lock (_gate)
        {
            _signOfLife?.Cancel();
            _signOfLife?.Dispose();
            current = new CancellationTokenSource();
            _signOfLife = current;
            arm = ++_signOfLifeArm;
        }

        _ = _RaiseSignOfLifeAsync(delay, arm, current.Token);
    }

    public void StopSignOfLife()
    {
        lock (_gate)
        {
            _signOfLife?.Cancel();
            _signOfLife?.Dispose();
            _signOfLife = null;
        }
    }

    public bool IsCurrentSignOfLife(int arm)
    {
        lock (_gate)
        {
            return _signOfLife is not null && _signOfLifeArm == arm;
        }
    }

    public ValueTask DisposeAsync()
    {
        StopPolling();
        StopListening();
        return ValueTask.CompletedTask;
    }

    public Task<bool> HasOutstandingBackgroundShellsAsync() => Task.FromResult(_Facts.HasOutstandingWork);

    // AC-1456: the rows this handle holds, for a pane drawing from it; a later upsert of the same row replaces it.
    public Task<SessionRowSnapshot?> ReadRowsAtAsync(Func<long> lastSeq)
    {
        lock (_gate)
        {
            return Task.FromResult<SessionRowSnapshot?>(new SessionRowSnapshot([.. _rows], _snapshotSeq));
        }
    }

    public async Task<SessionTranscriptSlice> ReadTranscriptAsync(int count)
    {
        var transcript = await _client.GetAsync<RemoteTranscript>($"{_Path}/transcript?count={count}").ConfigureAwait(false);
        return new SessionTranscriptSlice(
            [.. transcript.Entries.Select(entry => new SessionTranscriptEntry(entry.Kind, entry.Text, entry.ToolResult))],
            transcript.TotalEntries);
    }

    public async Task<bool> SendPromptAsync(string prompt) => await SubmitPromptWhenReadyAsync(prompt).ConfigureAwait(false) is not null;

    public async Task<bool?> SubmitPromptWhenReadyAsync(string prompt)
    {
        try
        {
            var sent = await _client.SendAsync<JsonElement>(HttpMethod.Post, $"{_Path}/prompt", new { text = prompt }).ConfigureAwait(false);
            return sent.TryGetProperty("delivered", out var delivered) && delivered.ValueKind == JsonValueKind.True;
        }
        catch (BackendApiException exception) when (exception.Status == HttpStatusCode.Conflict)
        {
            return null;
        }
    }

    public Task SetWorktreeBranchAsync(string? branch) =>
        throw new NotSupportedException("A remote session's worktree is its backend's own.");

    public async Task<bool> RespondToPermissionByIdAsync(string toolUseId, bool allow)
    {
        if (_isAssistant)
        {
            return false;
        }

        var answer = await _client.SendAsync<JsonElement>(HttpMethod.Post, $"{_Path}/permissions/{Uri.EscapeDataString(toolUseId)}", new { allow }).ConfigureAwait(false);
        return answer.TryGetProperty("answered", out var answered) && answered.ValueKind == JsonValueKind.True;
    }

    public Task<bool> FeedVerifyResultAsync(string caption, byte[] screenshotPng) =>
        throw new NotSupportedException("The API has no route to hand a verify render to a session.");

    // Read when asked, from the list the scope check already filters; the assistant's are not on it.
    public async Task<IReadOnlyList<SessionPendingPermission>> ReadPendingPermissionsAsync()
    {
        if (_isAssistant)
        {
            return [];
        }

        var list = await _client.GetAsync<RemoteSessionList>("api/v1/sessions").ConfigureAwait(false);
        return
        [
            .. list.Sessions
                .Where(row => string.Equals(row.PaneId, PaneId, StringComparison.Ordinal))
                .SelectMany(row => row.PendingPermissions ?? [])
                .Select(permission => new SessionPendingPermission(permission.ToolUseId, permission.Tool, permission.Input, permission.SinceUtc)),
        ];
    }

    public Task<bool> SetStatuslineAsync(string statusline) =>
        throw new NotSupportedException("The API has no route to set a session's statusline.");

    public Task<bool> SuggestNameAsync(string name) =>
        throw new NotSupportedException("The API has no route to name a session.");

    public Task<SessionWakeState> ReadWakeStateAsync() =>
        throw new NotSupportedException("A wake decision reads its fields in one instant on the backend's own machine.");

    private static Task _Unsupported(string message) => Task.FromException(new NotSupportedException(message));

    private async Task _SendQueueCommandAsync(string command, QueuedPrompt? prompt, bool takesMidTurnInput = false)
    {
        await _client.SendAsync<JsonElement>(HttpMethod.Post, $"{_Path}/queue", new
        {
            command,
            wireId = prompt?.WireId,
            text = prompt?.Text,
            replyToRowId = prompt?.ReplyToRowId,
            takesMidTurnInput,
        }).ConfigureAwait(false);
    }

    private async Task _SendQueueCommandAndReportAsync(string command, QueuedPrompt? prompt)
    {
        try
        {
            await _SendQueueCommandAsync(command, prompt).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (prompt is not null)
            {
                _Post(() => TurnFailedToStart?.Invoke(prompt, exception));
            }
        }
    }

    private async Task _DispatchAndReportAsync(QueuedPrompt prompt)
    {
        try
        {
            await ((ISessionControl)this).SendPromptAsync(OutgoingText(prompt)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _Post(() => TurnFailedToStart?.Invoke(prompt, exception));
        }
    }

    private async Task _RaiseSignOfLifeAsync(TimeSpan delay, int arm, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            if (IsCurrentSignOfLife(arm))
            {
                _Post(() => SignOfLifeDue?.Invoke(arm));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void _SetLocalQueue(IReadOnlyList<QueuedPrompt> queue)
    {
        lock (_gate)
        {
            _queue.Clear();
            _queue.AddRange(queue);
        }

        _Post(() => QueueChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset)));
    }

    private void _Post(Action action)
    {
        Action<Action>? post;
        lock (_gate)
        {
            post = _isListening ? _post : null;
        }

        post?.Invoke(action);
    }

    internal void Update(RemoteSessionRow facts)
    {
        lock (_gate)
        {
            _facts = facts;
            _usageStatus = facts.UsageStatus?.ToCore();
            _queue.Clear();
            _queue.AddRange((facts.Queue ?? []).Select(item => new QueuedPrompt(item.Text, [], wireId: item.WireId)));
        }
    }

    // The rows and the seq they stand at, read together by the route; a row on the stream at or below it is in them.
    // False when the pane closed since the list was read, so the registry leaves it out.
    internal async Task<bool> LoadSnapshotAsync(bool reload, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_loaded && !reload)
            {
                return true;
            }
        }

        RemoteTranscript transcript;
        try
        {
            transcript = await _client.GetAsync<RemoteTranscript>($"{_Path}/transcript?count=1", cancellationToken).ConfigureAwait(false);
        }
        catch (BackendApiException exception) when (exception.Status == HttpStatusCode.NotFound || (_isAssistant && exception.Status == HttpStatusCode.Forbidden))
        {
            return false;
        }

        IReadOnlyList<TranscriptSnapshotEntry> rows = transcript.Rows ?? [];
        var seq = transcript.Seq ?? 0;
        lock (_gate)
        {
            _rows.Clear();
            _rows.AddRange(rows);
            _snapshotSeq = seq;
            _loaded = true;
        }

        // A reload replaces rows a listener already drew; it hears each again as it now stands.
        if (reload)
        {
            foreach (var row in rows)
            {
                RowUpserted?.Invoke(new TranscriptRowUpsert(seq, 0, row));
            }
        }

        return true;
    }

    internal void Apply(BackendEvent evt)
    {
        Action? raise = null;
        lock (_gate)
        {
            switch (evt.Kind)
            {
                case "row" when evt.Seq > _snapshotSeq && evt.Data.Deserialize<RemoteRowEvent>(Json) is { Row: { } row } data:
                    var index = _rows.FindIndex(existing => string.Equals(existing.Id, row.Id, StringComparison.Ordinal));
                    if (index < 0)
                    {
                        _rows.Add(row);
                    }
                    else
                    {
                        _rows[index] = row;
                    }

                    var upsert = new TranscriptRowUpsert(data.Seq, data.Version, row);
                    raise = () => RowUpserted?.Invoke(upsert);
                    break;

                case "live-state" when evt.Data.Deserialize<RemoteLiveStateEvent>(Json) is { LiveState: { } liveState } live:
                    var busyChanged = _isBusy != (liveState.Status == SessionStatus.Busy);
                    _isBusy = liveState.Status == SessionStatus.Busy;
                    _liveState = liveState;
                    if (live.UsageStatus is { } liveUsage)
                    {
                        _usageStatus = liveUsage.ToCore();
                    }

                    raise = () =>
                    {
                        LiveStateChanged?.Invoke(liveState);
                        if (busyChanged)
                        {
                            BusyChanged?.Invoke();
                        }

                        if (live.UsageStatus is not null)
                        {
                            UsageCatchUpDue?.Invoke();
                        }
                    };
                    break;

                case "turn-ended" when evt.Data.Deserialize<RemoteTurnEndedEvent>(Json) is { End: { } end }:
                    raise = () => TurnEnded?.Invoke(end);
                    break;

                case "usage" when evt.Data.Deserialize<RemoteUsageEvent>(Json) is { UsageStatus: { } usageStatus }:
                    _usageStatus = usageStatus.ToCore();
                    raise = () => UsageCatchUpDue?.Invoke();
                    break;

                case "tool" when evt.Data.Deserialize<RemoteToolEvent>(Json) is { Call: { } call }:
                    raise = () =>
                    {
                        ToolProgressed?.Invoke();
                        ToolActivityProduced?.Invoke(call);
                    };
                    break;

                case "queue" when evt.Data.Deserialize<RemoteQueueEvent>(Json) is { Queue: { } queue }:
                    _queue.Clear();
                    _queue.AddRange(queue.Select(item => new QueuedPrompt(item.Text, [], wireId: item.WireId)));
                    raise = () => QueueChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
                    break;
            }
        }

        if (raise is not null)
        {
            _Post(raise);
        }
    }
}

internal sealed record RemoteTranscript(
    int TotalEntries,
    IReadOnlyList<RemoteTranscriptEntry> Entries,
    IReadOnlyList<TranscriptSnapshotEntry>? Rows = null,
    long? Seq = null);

internal sealed record RemoteTranscriptEntry(string Kind, string Text, string? ToolResult);

internal sealed record RemoteRowEvent(long Seq, int Version, TranscriptSnapshotEntry? Row);

internal sealed record RemoteLiveStateEvent(SessionLiveState? LiveState, RemoteUsageStatus? UsageStatus);

internal sealed record RemoteTurnEndedEvent(SessionTurnEnd? End);

internal sealed record RemoteUsageEvent(RemoteUsageStatus? UsageStatus);

internal sealed record RemoteToolEvent(SessionToolCall? Call);

internal sealed record RemoteQueueEvent(IReadOnlyList<RemoteQueueItem>? Queue);

internal sealed record RemoteQueueItem(string WireId, string Text);

internal sealed record RemoteUsageStatus(double? ContextUsedPercent, IReadOnlyList<RemoteRateWindow> RateLimits)
{
    public static RemoteUsageStatus? From(SessionStatusFeed? status) => status is null
        ? null
        : new RemoteUsageStatus(
            status.ContextUsedPercent,
            [.. status.RateLimits.Select(window => new RemoteRateWindow(
                window.Label,
                window.UsedPercent,
                window.ResetsAt,
                window.ThresholdPercent))]);

    public SessionStatusFeed ToCore() => new(
        ContextUsedPercent,
        [.. RateLimits.Select(window => new SessionRateWindow(
            window.Label,
            window.UsedPercent,
            window.ResetsAt,
            window.ThresholdPercent))]);
}

internal sealed record RemoteRateWindow(
    string Label,
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    double? ThresholdPercent);
