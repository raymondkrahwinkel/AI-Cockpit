using System.Net;
using System.Text.Json;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Workspaces;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1388 (F5.6a): the launcher, registry and event log of a backend reached over `/api/v1`. One SSE stream, read in
// order, keeps the registry and every handle current; nothing polls beside it. What only means something on the
// backend's own machine is refused, not faked.
public sealed class RemoteBackend : ISessionLauncher, ISessionRegistry, IBackendEventLog, IAsyncDisposable
{
    // The backend picks the desk a remote start lands on, so the client sees one: the node's.
    public const string NodeDeskId = "node";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static readonly WorkspaceSettings NodeDesk = new()
    {
        Workspaces = [new Workspace(NodeDeskId, "Node", WorkspaceType.Sessions)],
        ActiveWorkspaceId = NodeDeskId,
    };

    private readonly BackendApiClient _client;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private IReadOnlyList<RemoteSessionHandle> _sessions = [];
    private RemoteSessionHandle? _assistant;
    private TaskCompletionSource _refreshed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _lastSeq;
    private Task _reader = Task.CompletedTask;

    // AC-1456: told true once the stream's headers came back and false each time it dropped; null when nobody asked.
    private readonly Action<bool>? _connectionChanged;

    internal RemoteBackend(BackendApiClient client, Action<bool>? connectionChanged = null)
    {
        _client = client;
        _connectionChanged = connectionChanged;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<ISessionHandle> All
    {
        get
        {
            lock (_gate)
            {
                return _sessions;
            }
        }
    }

    public ISessionHandle? Assistant
    {
        get
        {
            lock (_gate)
            {
                return _assistant;
            }
        }
    }

    public long LastSeq
    {
        get
        {
            lock (_gate)
            {
                return _lastSeq;
            }
        }
    }

    public WorkspaceSettings Workspaces => NodeDesk;

    public IReadOnlyList<RemoteProviderUsageSignal>? UsageSignalsOf(string providerId)
    {
        lock (_gate)
        {
            return _sessions
                .Select(session => session.Facts)
                .Where(row => string.Equals(row.ProviderId, providerId, StringComparison.Ordinal))
                .Select(row => row.UsageSignals)
                .FirstOrDefault(signals => signals is not null);
        }
    }

    public bool CanSignIn(string profileLabel)
    {
        lock (_gate)
        {
            return _sessions.Any(session =>
                string.Equals(session.Facts.Profile, profileLabel, StringComparison.Ordinal)
                && session.Facts.CanSignIn);
        }
    }

    // AC-1456: true once the backend turned the key away; the stream then stays down until someone connects anew.
    public bool KeyRefused { get; private set; }

    // The list and every session's rows as they stand, then the stream from the list's seq on. The caller keeps the client.
    public static async Task<RemoteBackend> ConnectAsync(BackendApiClient client, Action<bool>? connectionChanged = null)
    {
        var backend = new RemoteBackend(client, connectionChanged);
        var seq = await backend._RefreshAsync(reload: false).ConfigureAwait(false);
        backend._reader = backend._FollowAsync(seq);
        return backend;
    }

    public ISessionHandle? Find(string paneId) => _Find(paneId);

    // Nothing here to exclude against: the backend checks each action's precondition again when it runs.
    public Task<T> RunExclusiveAsync<T>(Func<T> decision) => Task.FromResult(decision());

    public bool CanCloseWorkspace(string workspaceId) => false;

    public bool ProfileHasTtyRoute(SessionProfile profile) => false;

    public Task<Project?> FindProjectByIdAsync(string projectId) =>
        throw new NotSupportedException("A remote backend's projects are not on the API yet (F5.6b, AC-1446).");

    public async Task<LaunchedSession?> StartSessionAsync(SessionLaunchRequest request)
    {
        // AC-1448: a chosen pane or a resume would load a recorded transcript, which needs a scope check first (AC-1446).
        if (request.PaneId is not null || request.Resume is not null)
        {
            throw new ArgumentException("A remote start cannot choose its pane or resume one; neither goes over the line.", nameof(request));
        }

        JsonElement started;
        try
        {
            started = await _client.SendAsync<JsonElement>(
                HttpMethod.Post,
                "api/v1/sessions",
                new { profile = request.Profile.Label, projectId = request.ProjectId, prompt = request.Prompt, name = request.SessionName }).ConfigureAwait(false);
        }
        catch (BackendApiException exception) when (exception.Status == HttpStatusCode.Conflict)
        {
            return null;
        }

        var paneId = started.GetProperty("paneId").GetString() ?? throw new JsonException("The backend started a session without a pane id.");
        await _AwaitRegistryAsync(() => _Find(paneId) is not null).ConfigureAwait(false);
        return new LaunchedSession(
            paneId,
            started.GetProperty("name").GetString() ?? paneId,
            started.TryGetProperty("promptDelivered", out var delivered) && delivered.ValueKind is JsonValueKind.True or JsonValueKind.False ? delivered.GetBoolean() : null);
    }

    public async Task StopSessionAsync(string paneId)
    {
        try
        {
            await _client.SendAsync<JsonElement>(HttpMethod.Delete, $"api/v1/sessions/{Uri.EscapeDataString(paneId)}", null).ConfigureAwait(false);
        }
        catch (BackendApiException exception) when (exception.Status == HttpStatusCode.NotFound)
        {
            return;
        }

        await _AwaitRegistryAsync(() => _Find(paneId) is null).ConfigureAwait(false);
    }

    public Task<bool> SetSessionNameAsync(string paneId, string name) =>
        throw new NotSupportedException("The API has no route to rename a session.");

    public Task<Workspace> CreateSessionsWorkspaceAsync(string name) =>
        throw new NotSupportedException("A remote backend's desks are its own; a remote start lands on the one it picks.");

    public Task RenameWorkspaceAsync(string workspaceId, string name) =>
        throw new NotSupportedException("A remote backend's desks are its own.");

    public Task<int> CloseWorkspaceIfEmptyAsync(string workspaceId) =>
        throw new NotSupportedException("A remote backend's desks are its own.");

    public long Append(string kind, string? paneId, object data, string? profileLabel = null, string? projectId = null) =>
        throw new NotSupportedException("A remote event log is written by its backend only.");

    public IAsyncEnumerable<BackendEvent> ReadFromAsync(long afterSeq, CancellationToken cancellationToken) =>
        _client.StreamEventsAsync(afterSeq < 0 ? null : afterSeq, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _reader.ConfigureAwait(false);
        _stop.Dispose();
    }

    private RemoteSessionHandle? _Find(string paneId)
    {
        lock (_gate)
        {
            return _sessions.FirstOrDefault(session => string.Equals(session.PaneId, paneId, StringComparison.Ordinal));
        }
    }

    // Anything that breaks the loop, a failed refresh or a throwing listener, starts it again from a full reload; only a
    // refused key ends it, and a waiting start or stop hears why.
    private async Task _FollowAsync(long afterSeq)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                await _ReadAsync(afterSeq).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (BackendApiException exception) when (exception.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                lock (_gate)
                {
                    _refreshed.TrySetException(exception);
                }

                KeyRefused = true;
                _connectionChanged?.Invoke(false);
                return;
            }
            catch (Exception)
            {
                // A failed refresh is a drop the stream did not see; the reload below brings every handle back.
                _connectionChanged?.Invoke(false);
            }

            try
            {
                await Task.Delay(backoff, _stop.Token).ConfigureAwait(false);
                afterSeq = await _RefreshAsync(reload: true).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
        }
    }

    private async Task _ReadAsync(long afterSeq)
    {
        await foreach (var evt in _client.StreamEventsAsync(afterSeq, _stop.Token, _connectionChanged).ConfigureAwait(false))
        {
            lock (_gate)
            {
                _lastSeq = Math.Max(_lastSeq, evt.Seq);
            }

            if (evt.Kind is "sessions-changed" or "reset")
            {
                await _RefreshAsync(reload: evt.Kind == "reset").ConfigureAwait(false);
            }
            else if (evt.PaneId is { } paneId && (_Find(paneId) ?? _AssistantAt(paneId)) is { } handle)
            {
                handle.Apply(evt);
            }
        }
    }

    private RemoteSessionHandle? _AssistantAt(string paneId)
    {
        lock (_gate)
        {
            return _assistant is { } assistant && string.Equals(assistant.PaneId, paneId, StringComparison.Ordinal) ? assistant : null;
        }
    }

    // A handle seen before keeps its rows and its listeners; a new one, or every one after a reset, takes its snapshot first.
    private async Task<long> _RefreshAsync(bool reload)
    {
        var list = await _client.GetAsync<RemoteSessionList>("api/v1/sessions").ConfigureAwait(false);
        List<RemoteSessionHandle> sessions = [];
        foreach (var row in list.Sessions)
        {
            var handle = _Find(row.PaneId) ?? new RemoteSessionHandle(_client, row.PaneId, isAssistant: false);
            handle.Update(row);
            if (await handle.LoadSnapshotAsync(reload).ConfigureAwait(false))
            {
                sessions.Add(handle);
            }
        }

        RemoteSessionHandle? assistant = null;
        if (list.Assistant is { } assistantRow)
        {
            var candidate = _AssistantAt(assistantRow.PaneId) ?? new RemoteSessionHandle(_client, assistantRow.PaneId, isAssistant: true);
            candidate.Update(new RemoteSessionRow(assistantRow.PaneId, assistantRow.Name));
            assistant = await candidate.LoadSnapshotAsync(reload).ConfigureAwait(false) ? candidate : null;
        }

        TaskCompletionSource refreshed;
        lock (_gate)
        {
            _sessions = sessions;
            _assistant = assistant;
            _lastSeq = Math.Max(_lastSeq, list.Seq);
            refreshed = _refreshed;
            _refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        refreshed.TrySetResult();
        Changed?.Invoke(this, EventArgs.Empty);
        return list.Seq;
    }

    // A start or stop returns once the stream has carried its change into the registry, not before.
    private async Task _AwaitRegistryAsync(Func<bool> settled)
    {
        using var patience = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        patience.CancelAfter(Patience);
        while (!settled())
        {
            Task refreshed;
            lock (_gate)
            {
                refreshed = _refreshed.Task;
            }

            if (settled())
            {
                return;
            }

            await refreshed.WaitAsync(patience.Token).ConfigureAwait(false);
        }
    }
}

internal sealed record RemoteSessionList(long Seq, RemoteAssistantRow? Assistant, IReadOnlyList<RemoteSessionRow> Sessions);

internal sealed record RemoteAssistantRow(string PaneId, string Name);

internal sealed record RemoteSessionRow(
    string PaneId,
    string Name,
    string? Profile = null,
    string? ProjectId = null,
    string? Statusline = null,
    string? Status = null,
    bool HasOutstandingWork = false,
    IReadOnlyList<RemotePendingPermission>? PendingPermissions = null,
    IReadOnlyList<RemoteQueueItem>? Queue = null,
    string? ProviderId = null,
    IReadOnlyList<RemoteProviderUsageSignal>? UsageSignals = null,
    RemoteUsageStatus? UsageStatus = null,
    bool CanSignIn = false);

internal sealed record RemotePendingPermission(string ToolUseId, string Tool, string Input, DateTimeOffset SinceUtc);

public sealed record RemoteProviderUsageSignal(
    string Key,
    string Label,
    string Kind,
    double DefaultThresholdPercent,
    string? Description,
    bool SupportsResume,
    string? DefaultResumePrompt);
