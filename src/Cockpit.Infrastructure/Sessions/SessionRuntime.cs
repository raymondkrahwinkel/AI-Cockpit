using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;

namespace Cockpit.Infrastructure.Sessions;

// Owns one session's driver and pumps its events on a plain task — no Dispatcher, no ObservableCollection,
// nothing that assumes a UI is watching. The session panel subscribes to `EventAppended`; a delegated task
// (#67) polls `EventsSince` — one session implementation serving both watchers.
internal sealed class SessionRuntime : ISessionRuntime
{
    // How many events the log keeps. A long-running session would otherwise grow without bound, since every
    // text delta is an event. Dropping the oldest costs a late consumer some early detail; status,
    // capabilities and `LastAssistantText` are folded as events arrive and stay correct.
    private const int MaxLoggedEvents = 5_000;

    private readonly ISessionDriverFactory _driverFactory;
    private readonly ISessionMemoryLimiter? _memoryLimiter;
    private readonly List<SessionEvent> _events = [];
    private readonly List<string> _currentTurnText = [];
    private readonly Lock _eventsLock = new();

    private ISessionDriver? _driver;
    private CancellationTokenSource? _lifetime;
    private Task? _pump;

    // The OS ceiling around this session's process tree, while it has one (AC-661).
    private IDisposable? _memoryCap;

    // The conversation id the driver last reported, so an event Cockpit raises itself lands on the same
    // conversation as the driver's own (AC-1060).
    private string? _lastSessionId;

    // AC-1060: the cgroup this session runs in, taken while it is still alive. After an oomd kill there is no
    // process left to ask, and the journal names the group rather than the pid.
    private string? _cgroupName;

    // Events dropped off the front of the log, so a cursor handed out before a trim still maps to the right
    // place in the log rather than silently replaying events the consumer has already seen.
    private int _droppedEvents;

    public SessionRuntime(ISessionDriverFactory driverFactory, SessionProfile? profile, ISessionMemoryLimiter? memoryLimiter = null)
    {
        _driverFactory = driverFactory;
        Profile = profile;
        _memoryLimiter = memoryLimiter;
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public SessionProfile? Profile { get; private set; }

    public SessionCapabilities? Capabilities => _driver?.Capabilities;

    // The process this session runs in, once its driver started one (#78) — null for a provider that is an HTTP call rather than a process.
    public int? ProcessId => _driver?.ProcessId;

    public SessionStatusFeed? CurrentStatus => _driver?.CurrentStatus;

    public IReadOnlyList<SessionLiveOption> LiveOptions => _driver?.LiveOptions ?? [];

    // AC-693: the pump ends when the driver's stream does, so a death out of band shows here, not only at DisposeAsync.
    public bool IsRunning => _pump is { IsCompleted: false };

    public string? LastAssistantText { get; private set; }

    public event Action<SessionEvent>? EventAppended;

    public (IReadOnlyList<SessionEvent> Events, int NextCursor) EventsSince(int cursor)
    {
        lock (_eventsLock)
        {
            var skip = Math.Max(0, cursor - _droppedEvents);
            var events = skip >= _events.Count ? [] : _events.Skip(skip).ToArray();
            return (events, _droppedEvents + _events.Count);
        }
    }

    public async Task StartAsync(
        SessionProfile? profile,
        string? permissionMode = null,
        string? model = null,
        IReadOnlySet<string>? enabledMcpServerNames = null,
        string? workingDirectory = null,
        SessionResume? resume = null,
        IReadOnlyDictionary<string, string>? launchOptions = null,
        string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        Profile = profile;
        _lifetime = new CancellationTokenSource();

        // Worktree isolation is resolved by the cockpit before the session starts (AC-85), keyed on the pane the
        // session runs in, so the runtime just launches the driver in whatever working directory it is handed —
        // whether that is the folder as given or the isolated worktree the cockpit already created for it.

        // Picking the driver is deferred to here rather than to the constructor: it depends on the profile's
        // provider, and a profile pointing at a missing plugin provider throws — which the caller wants to see
        // as a failed start, not as a failed construction.
        _driver = _driverFactory.Create(profile);
        await _driver.StartAsync(profile, permissionMode, model, enabledMcpServerNames, workingDirectory, resume, launchOptions, projectId, _lifetime.Token);

        // AC-661: cap the driver's own child (a spawned CLI) the moment it exists, before it has run a turn and
        // spawned anything itself. A provider that is an HTTP call has no process and nothing to cap.
        if (_memoryLimiter is not null && _driver.ProcessId is { } processId)
        {
            _memoryCap = _memoryLimiter.Apply(processId, SessionMemoryCap.ResolveBytes(profile, launchOptions));
            _cgroupName = LinuxSessionCgroup.NameFor(processId);
        }

        _pump = _PumpEventsAsync(_lifetime.Token);
    }

    public Task SendUserMessageAsync(string text, IReadOnlyList<ImageAttachment>? images = null, CancellationToken cancellationToken = default) =>
        _driver?.SendUserMessageAsync(text, images, cancellationToken) ?? Task.CompletedTask;

    public Task InterruptAsync(CancellationToken cancellationToken = default) =>
        _driver?.InterruptAsync(cancellationToken) ?? Task.CompletedTask;

    public Task CompactContextAsync(CancellationToken cancellationToken = default) =>
        _driver?.CompactContextAsync(cancellationToken) ?? Task.CompletedTask;

    public Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default) =>
        _driver?.SetPermissionModeAsync(mode, cancellationToken) ?? Task.CompletedTask;

    public Task SetModelAsync(string? model, CancellationToken cancellationToken = default) =>
        _driver?.SetModelAsync(model, cancellationToken) ?? Task.CompletedTask;

    public Task SetMaxThinkingTokensAsync(int maxThinkingTokens, CancellationToken cancellationToken = default) =>
        _driver?.SetMaxThinkingTokensAsync(maxThinkingTokens, cancellationToken) ?? Task.CompletedTask;

    public Task SetLiveOptionAsync(string key, string value, CancellationToken cancellationToken = default) =>
        _driver?.SetLiveOptionAsync(key, value, cancellationToken) ?? Task.CompletedTask;

    public Task SetAutoApproveToolsAsync(bool autoApprove, CancellationToken cancellationToken = default) =>
        _driver?.SetAutoApproveToolsAsync(autoApprove, cancellationToken) ?? Task.CompletedTask;

    public Task SetDelegatedToolGateAsync(string ceiling, IReadOnlyList<string> allowedTools, CancellationToken cancellationToken = default) =>
        _driver?.SetDelegatedToolGateAsync(ceiling, allowedTools, cancellationToken) ?? Task.CompletedTask;

    public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default) =>
        _driver?.RespondToPermissionAsync(toolUseId, allow, cancellationToken) ?? Task.CompletedTask;

    public Task RespondToPermissionAsync(string toolUseId, bool allow, string? answersJson, CancellationToken cancellationToken) =>
        _driver?.RespondToPermissionAsync(toolUseId, allow, answersJson, cancellationToken) ?? Task.CompletedTask;

    public Task AllowPermissionAlwaysAsync(string toolUseId, string toolName, string inputJson, PermissionRuleScope scope, CancellationToken cancellationToken = default) =>
        _driver?.AllowPermissionAlwaysAsync(toolUseId, toolName, inputJson, scope, cancellationToken) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        // Interrupt first so a running turn is told to stop rather than having its process pulled from under
        // it; then cancel the pump, then let the driver tear its process down.
        if (_driver is not null)
        {
            try
            {
                await _driver.InterruptAsync();
            }
            catch (Exception)
            {
                // Best-effort: a session that is already gone must not make closing it throw.
            }
        }

        if (_lifetime is not null)
        {
            await _lifetime.CancelAsync();
        }

        if (_pump is not null)
        {
            try
            {
                await _pump;
            }
            catch (OperationCanceledException)
            {
                // Expected: cancelling the lifetime is how the pump ends.
            }

            _pump = null;
        }

        if (_driver is not null)
        {
            await _driver.DisposeAsync();
            _driver = null;
        }

        // After the driver, so the job object/cgroup is empty by the time it is released.
        _memoryCap?.Dispose();
        _memoryCap = null;

        _lifetime?.Dispose();
        _lifetime = null;
    }

    private async Task _PumpEventsAsync(CancellationToken cancellationToken)
    {
        if (_driver is null)
        {
            return;
        }

        try
        {
            await foreach (var evt in _driver.Events.WithCancellation(cancellationToken))
            {
                _Publish(evt);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
            return;
        }

        await _ReportOomdKillAsync(cancellationToken);
    }

    // AC-1060: the stream ended on its own. `systemd-oomd` kills the whole cgroup, so there is no exit code and
    // no last line to read — its journal naming this session's group is the only fact that says what happened.
    private async Task _ReportOomdKillAsync(CancellationToken cancellationToken)
    {
        // Silence unless there is evidence: a session the operator closed also ends here when its process goes
        // before the lifetime is cancelled, and a guessed cause is what criterion 2 exists to avoid.
        if (cancellationToken.IsCancellationRequested || _cgroupName is not { } group)
        {
            return;
        }

        if (await LinuxOomdJournal.FindKillAsync(group) is not { } kill)
        {
            return;
        }

        var because = kill.Pressure.Length > 0
            ? $" — memory pressure in the slice it sits in was {kill.Pressure}"
            : string.Empty;

        _Publish(new SessionError
        {
            // The conversation this row belongs to, as the driver last reported it — the kill itself carries no
            // session id, and a row without one is attributed to no conversation at all.
            SessionId = _lastSessionId,
            Message = "This session was killed by systemd-oomd, not by anything in the session or in Cockpit. "
                + $"Its whole cgroup ({kill.CgroupName}) went at once{because}.",
        });
    }

    private void _Publish(SessionEvent evt)
    {
        _Append(evt);
        EventAppended?.Invoke(evt);
    }

    // A turn can produce several assistant-text blocks, so the reply is folded as they complete and published
    // once the turn ends, giving "the result" as the whole answer rather than a fragment. TurnCompleted.Result
    // is preferred when the driver provides one, falling back to the collected prose.
    private void _Append(SessionEvent evt)
    {
        _lastSessionId = evt.SessionId ?? _lastSessionId;

        switch (evt)
        {
            case AssistantTextCompleted { Text.Length: > 0 } text:
                _currentTurnText.Add(text.Text);
                break;

            case TurnCompleted turn:
                var result = !string.IsNullOrWhiteSpace(turn.Result)
                    ? turn.Result
                    : _currentTurnText.Count > 0 ? string.Join("\n\n", _currentTurnText) : null;
                if (result is not null)
                {
                    LastAssistantText = result;
                }

                _currentTurnText.Clear();
                break;
        }

        lock (_eventsLock)
        {
            _events.Add(evt);
            if (_events.Count > MaxLoggedEvents)
            {
                _events.RemoveAt(0);
                _droppedEvents++;
            }
        }
    }
}
