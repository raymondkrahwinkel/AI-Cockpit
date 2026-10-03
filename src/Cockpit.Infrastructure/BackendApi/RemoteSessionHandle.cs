using System.Net;
using System.Text.Json;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Sessions;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1388: one session on a backend reached over `/api/v1`. Its facts are the session list's as `RemoteBackend` last
// read it; its rows the transcript route's snapshot plus every later row the stream carries, each exactly once. A
// member the API has no route for refuses; one that only means something on the backend's machine reads empty.
public sealed class RemoteSessionHandle : ISessionHandle
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly BackendApiClient _client;
    private readonly bool _isAssistant;
    private readonly Lock _gate = new();
    private readonly List<TranscriptSnapshotEntry> _rows = [];
    private RemoteSessionRow _facts;
    private SessionLiveState? _liveState;
    private long _snapshotSeq;
    private bool _loaded;

    internal RemoteSessionHandle(BackendApiClient client, string paneId, bool isAssistant)
    {
        _client = client;
        _isAssistant = isAssistant;
        _facts = new RemoteSessionRow(paneId, paneId);
    }

    public event Action<TranscriptRowUpsert>? RowUpserted;

    public event Action<SessionLiveState>? LiveStateChanged;

    public event Action<SessionToolCall>? ToolActivityProduced;

    public string PaneId => _Facts.PaneId;

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

    private string _Path => _isAssistant ? "api/v1/assistant" : $"api/v1/sessions/{Uri.EscapeDataString(PaneId)}";

    public Task<bool> HasOutstandingBackgroundShellsAsync() => Task.FromResult(_Facts.HasOutstandingWork);

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

    internal void Update(RemoteSessionRow facts)
    {
        lock (_gate)
        {
            _facts = facts;
        }
    }

    // The rows and the seq they stand at, read together by the route; a row on the stream at or below it is in them.
    internal async Task LoadSnapshotAsync(bool reload)
    {
        lock (_gate)
        {
            if (_loaded && !reload)
            {
                return;
            }
        }

        var transcript = await _client.GetAsync<RemoteTranscript>($"{_Path}/transcript?count=1").ConfigureAwait(false);
        lock (_gate)
        {
            _rows.Clear();
            _rows.AddRange(transcript.Rows ?? []);
            _snapshotSeq = transcript.Seq ?? 0;
            _loaded = true;
        }
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

                case "live-state" when evt.Data.Deserialize<RemoteLiveStateEvent>(Json) is { LiveState: { } liveState }:
                    _liveState = liveState;
                    raise = () => LiveStateChanged?.Invoke(liveState);
                    break;

                case "tool" when evt.Data.Deserialize<RemoteToolEvent>(Json) is { Call: { } call }:
                    raise = () => ToolActivityProduced?.Invoke(call);
                    break;
            }
        }

        raise?.Invoke();
    }
}

internal sealed record RemoteTranscript(
    int TotalEntries,
    IReadOnlyList<RemoteTranscriptEntry> Entries,
    IReadOnlyList<TranscriptSnapshotEntry>? Rows = null,
    long? Seq = null);

internal sealed record RemoteTranscriptEntry(string Kind, string Text, string? ToolResult);

internal sealed record RemoteRowEvent(long Seq, int Version, TranscriptSnapshotEntry? Row);

internal sealed record RemoteLiveStateEvent(SessionLiveState? LiveState);

internal sealed record RemoteToolEvent(SessionToolCall? Call);
