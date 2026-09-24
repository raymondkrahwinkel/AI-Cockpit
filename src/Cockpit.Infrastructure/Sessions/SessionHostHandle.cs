using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// AC-1378: one SDK session without a view model, as the registry hands it out, and the consumer its host asks for:
// it applies every event to the transcript and ends the turn, the duties `SessionViewModel.Apply` has on the desktop.
// One lock stands in for the desktop's UI thread, so the fold, the turn gate and the reads never interleave.
public sealed class SessionHostHandle : ISessionHandle, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly SessionHost<QueuedPrompt> _host;
    private readonly bool _nameIsChosen;
    private readonly List<TranscriptSnapshotEntry> _rows = [];
    private string _title;
    private string _statusline = string.Empty;
    private string? _worktreeBranch;

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

    public async Task<bool> SendPromptAsync(string prompt)
    {
        Task sending;
        lock (_gate)
        {
            if (_host.Runtime is not { IsRunning: true } || _host.TurnsHeldBecause is not null)
            {
                return false;
            }

            sending = _host.SubmitAsync(new QueuedPrompt(prompt, []));
        }

        await sending.ConfigureAwait(false);
        return true;
    }

    // Nothing is ever held here, so a prompt the session cannot take now is accepted by nobody.
    public async Task<bool?> SubmitPromptWhenReadyAsync(string prompt) =>
        await SendPromptAsync(prompt).ConfigureAwait(false) ? true : null;

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
    }

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
    }
}
