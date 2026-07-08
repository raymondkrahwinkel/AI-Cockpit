using Microsoft.Extensions.Logging;
using Tmds.DBus;
using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.Infrastructure.Voice.GlobalHotkey;

/// <summary>
/// Global push-to-talk via the XDG desktop portal's <c>org.freedesktop.portal.GlobalShortcuts</c>
/// interface — the sandboxed-safe way for a desktop app to get a system-wide hotkey on Wayland, where
/// nothing can install a raw keyboard hook. Ported 1:1 from the working spike
/// (<c>spike1_portal_hotkey.py</c>, live-confirmed on KDE Plasma 6.7/KWin): CreateSession, then
/// BindShortcuts with a preferred-trigger hint, then listen for Activated/Deactivated on that session —
/// Activated fires on physical key-down, Deactivated on key-up, exactly the hold semantics push-to-talk
/// needs. The actual key binding is owned by the compositor's own shortcut settings; the preferred
/// trigger is only a hint the portal may or may not honour (KDE binds it directly).
/// </summary>
internal sealed class PortalGlobalHotkeyService(ILogger<PortalGlobalHotkeyService> logger) : IGlobalHotkeyService
{
    private const string BusName = "org.freedesktop.portal.Desktop";
    private const string ShortcutId = "cockpit_push_to_talk";
    private static readonly ObjectPath DesktopPath = new("/org/freedesktop/portal/desktop");

    private Connection? _connection;
    private IDisposable? _activatedWatch;
    private IDisposable? _deactivatedWatch;
    private string _requestSender = string.Empty;
    private int _requestCounter;
    private bool _isHolding;

    public event EventHandler? HoldStarted;
    public event EventHandler? HoldEnded;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
        {
            return;
        }

        _connection = new Connection(Address.Session);
        var info = await _connection.ConnectAsync().ConfigureAwait(false);
        // Portal request object paths are namespaced under the caller's own unique bus name, with the
        // leading ':' stripped and '.' turned into '_' — see the portal spec and the spike this ports.
        _requestSender = info.LocalName.TrimStart(':').Replace('.', '_');

        var shortcuts = _connection.CreateProxy<IGlobalShortcutsPortal>(BusName, DesktopPath);

        var sessionHandle = await _CallPortalRequestAsync(
            token => shortcuts.CreateSessionAsync(new Dictionary<string, object>
            {
                ["handle_token"] = token,
                ["session_handle_token"] = _NextToken("sess"),
            }),
            // The GlobalShortcuts portal returns session_handle as a plain string ('s'), not an object
            // path ('o') — a long-standing quirk of this portal interface — so wrap it rather than cast.
            results => new ObjectPath((string)results["session_handle"])).ConfigureAwait(false);

        await _CallPortalRequestAsync(
            token => shortcuts.BindShortcutsAsync(
                sessionHandle,
                [(ShortcutId, new Dictionary<string, object>
                {
                    ["description"] = "Push to talk (hold)",
                    ["preferred_trigger"] = "F9",
                })],
                string.Empty,
                new Dictionary<string, object> { ["handle_token"] = token }),
            static _ => true).ConfigureAwait(false);

        _activatedWatch = await shortcuts.WatchActivatedAsync(_OnActivated).ConfigureAwait(false);
        _deactivatedWatch = await shortcuts.WatchDeactivatedAsync(_OnDeactivated).ConfigureAwait(false);
        logger.LogInformation("Global push-to-talk registered via the XDG GlobalShortcuts portal.");
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _activatedWatch?.Dispose();
        _deactivatedWatch?.Dispose();
        _activatedWatch = null;
        _deactivatedWatch = null;
        _connection?.Dispose();
        _connection = null;
        return Task.CompletedTask;
    }

    private void _OnActivated((ObjectPath Session, string ShortcutId, ulong Timestamp, IDictionary<string, object> Options) activation)
    {
        if (activation.ShortcutId != ShortcutId || _isHolding)
        {
            return;
        }

        _isHolding = true;
        HoldStarted?.Invoke(this, EventArgs.Empty);
    }

    private void _OnDeactivated((ObjectPath Session, string ShortcutId, ulong Timestamp, IDictionary<string, object> Options) deactivation)
    {
        if (deactivation.ShortcutId != ShortcutId || !_isHolding)
        {
            return;
        }

        _isHolding = false;
        HoldEnded?.Invoke(this, EventArgs.Empty);
    }

    // Portal calls are two-step: the method itself only returns a Request object handle; the actual
    // result arrives on that Request's Response signal (see the spike's _call helper). This subscribes
    // to the response before invoking the method, so the response signal can never race the subscription.
    private async Task<T> _CallPortalRequestAsync<T>(
        Func<string, Task<ObjectPath>> invoke,
        Func<IDictionary<string, object>, T> project)
    {
        var connection = _connection ?? throw new InvalidOperationException($"{nameof(PortalGlobalHotkeyService)} is not connected.");
        var token = _NextToken("req");
        var requestPath = new ObjectPath($"/org/freedesktop/portal/desktop/request/{_requestSender}/{token}");
        var request = connection.CreateProxy<IPortalRequest>(BusName, requestPath);

        var responseSource = new TaskCompletionSource<IDictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var responseWatch = await request.WatchResponseAsync(response =>
        {
            if (response.ResponseCode == 0)
            {
                responseSource.TrySetResult(response.Results);
            }
            else
            {
                responseSource.TrySetException(new InvalidOperationException($"Portal request failed with response code {response.ResponseCode}."));
            }
        }).ConfigureAwait(false);

        await invoke(token).ConfigureAwait(false);
        var results = await responseSource.Task.ConfigureAwait(false);
        return project(results);
    }

    private string _NextToken(string prefix) => $"{prefix}{Interlocked.Increment(ref _requestCounter)}";
}
