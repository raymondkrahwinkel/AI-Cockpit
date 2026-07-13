using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// Owns one session's driver and pumps its events on a plain task — no Dispatcher, no ObservableCollection,
/// nothing that assumes a UI is watching. Every consumer sits on the same runtime: the session panel
/// subscribes to <see cref="EventAppended"/> and marshals to the UI thread itself, a delegated task (#67)
/// polls <see cref="EventsSince"/>. That is the whole point of the split — one session implementation, two
/// kinds of watcher, rather than a headless copy of the interactive one.
/// </summary>
internal sealed class SessionRuntime : ISessionRuntime
{
    /// <summary>
    /// How many events the log keeps. A long-running session would otherwise grow without bound, since every
    /// text delta is an event. Dropping the oldest costs a late consumer some early detail; status,
    /// capabilities and <see cref="LastAssistantText"/> are folded as events arrive and stay correct.
    /// </summary>
    private const int MaxLoggedEvents = 5_000;

    private readonly ISessionDriverFactory _driverFactory;
    private readonly List<SessionEvent> _events = [];
    private readonly List<string> _currentTurnText = [];
    private readonly Lock _eventsLock = new();

    private ISessionDriver? _driver;
    private CancellationTokenSource? _lifetime;
    private Task? _pump;

    // Events dropped off the front of the log, so a cursor handed out before a trim still maps to the right
    // place in the log rather than silently replaying events the consumer has already seen.
    private int _droppedEvents;

    public SessionRuntime(ISessionDriverFactory driverFactory, SessionProfile? profile)
    {
        _driverFactory = driverFactory;
        Profile = profile;
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public SessionProfile? Profile { get; private set; }

    public SessionCapabilities? Capabilities => _driver?.Capabilities;

    /// <summary>The process this session runs in, once its driver started one (#78) — null for a provider that is an HTTP call rather than a process.</summary>
    public int? ProcessId => _driver?.ProcessId;

    public bool IsRunning => _pump is not null;

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
        CancellationToken cancellationToken = default)
    {
        Profile = profile;
        _lifetime = new CancellationTokenSource();

        // Picking the driver is deferred to here rather than to the constructor: it depends on the profile's
        // provider, and a profile pointing at a missing plugin provider throws — which the caller wants to see
        // as a failed start, not as a failed construction.
        _driver = _driverFactory.Create(profile);
        await _driver.StartAsync(profile, permissionMode, model, enabledMcpServerNames, workingDirectory, resume, _lifetime.Token);
        _pump = _PumpEventsAsync(_lifetime.Token);
    }

    public Task SendUserMessageAsync(string text, IReadOnlyList<ImageAttachment>? images = null, CancellationToken cancellationToken = default) =>
        _driver?.SendUserMessageAsync(text, images, cancellationToken) ?? Task.CompletedTask;

    public Task InterruptAsync(CancellationToken cancellationToken = default) =>
        _driver?.InterruptAsync(cancellationToken) ?? Task.CompletedTask;

    public Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default) =>
        _driver?.SetPermissionModeAsync(mode, cancellationToken) ?? Task.CompletedTask;

    public Task SetModelAsync(string? model, CancellationToken cancellationToken = default) =>
        _driver?.SetModelAsync(model, cancellationToken) ?? Task.CompletedTask;

    public Task SetMaxThinkingTokensAsync(int maxThinkingTokens, CancellationToken cancellationToken = default) =>
        _driver?.SetMaxThinkingTokensAsync(maxThinkingTokens, cancellationToken) ?? Task.CompletedTask;

    public Task SetAutoApproveToolsAsync(bool autoApprove, CancellationToken cancellationToken = default) =>
        _driver?.SetAutoApproveToolsAsync(autoApprove, cancellationToken) ?? Task.CompletedTask;

    public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default) =>
        _driver?.RespondToPermissionAsync(toolUseId, allow, cancellationToken) ?? Task.CompletedTask;

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
                _Append(evt);
                EventAppended?.Invoke(evt);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    // A turn can produce several assistant-text blocks (text, tool call, more text), so the reply is folded as
    // the blocks complete and only published once the turn ends — a consumer asking for "the result" then gets
    // the whole answer, not whichever fragment happened to be last. TurnCompleted.Result is preferred when the
    // driver reports one (the CLI's own final result), falling back to the prose we collected.
    private void _Append(SessionEvent evt)
    {
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
