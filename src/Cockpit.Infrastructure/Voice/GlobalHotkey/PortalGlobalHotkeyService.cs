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
internal sealed class PortalGlobalHotkeyService(IVoiceSettingsStore voiceSettingsStore, ILogger<PortalGlobalHotkeyService> logger) : IGlobalHotkeyService
{
    private const string BusName = "org.freedesktop.portal.Desktop";
    private const string ShortcutId = "cockpit_push_to_talk";
    private static readonly ObjectPath DesktopPath = new("/org/freedesktop/portal/desktop");

    private Connection? _connection;
    private IDisposable? _activatedWatch;
    private IDisposable? _deactivatedWatch;
    private IDisposable? _shortcutsChangedWatch;
    private string _requestSender = string.Empty;
    private int _requestCounter;
    private bool _isHolding;

    public event EventHandler? HoldStarted;
    public event EventHandler? HoldEnded;
    public event EventHandler? TriggerDescriptionChanged;

    /// <summary>What the compositor bound, in its own words. Null until <see cref="StartAsync"/> has asked it.</summary>
    public string? TriggerDescription { get; private set; }

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

        // The operator's configured key, not a constant. It used to hard-code "F9" and never read the setting at
        // all, so changing the push-to-talk key in Options did nothing here — not now, not after a restart.
        var settings = await voiceSettingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        await _CallPortalRequestAsync(
            token => shortcuts.BindShortcutsAsync(
                sessionHandle,
                [(ShortcutId, new Dictionary<string, object>
                {
                    ["description"] = "Push to talk (hold)",
                    // A hint, and the spec says so: the compositor may bind something else, or leave it to the
                    // operator. Which is why the answer is asked for below rather than assumed from this.
                    ["preferred_trigger"] = settings.PushToTalkKeyName,
                })],
                string.Empty,
                new Dictionary<string, object> { ["handle_token"] = token }),
            static _ => true).ConfigureAwait(false);

        _activatedWatch = await shortcuts.WatchActivatedAsync(_OnActivated).ConfigureAwait(false);
        _deactivatedWatch = await shortcuts.WatchDeactivatedAsync(_OnDeactivated).ConfigureAwait(false);
        _shortcutsChangedWatch = await shortcuts.WatchShortcutsChangedAsync(_OnShortcutsChanged).ConfigureAwait(false);

        await _RefreshTriggerDescriptionAsync(shortcuts, sessionHandle).ConfigureAwait(false);

        logger.LogInformation(
            "Global push-to-talk registered via the XDG GlobalShortcuts portal; asked for '{Preferred}', bound to '{Bound}'.",
            settings.PushToTalkKeyName,
            TriggerDescription ?? "<nothing yet — bind it in your desktop's shortcut settings>");
    }

    /// <summary>
    /// Asks the compositor what it bound. This is the only place that answer exists: the preferred trigger is a
    /// hint, and on a desktop that leaves the binding to its own shortcut settings the honest answer is that
    /// nothing is bound until the operator does it — which is a thing to say, not to guess at.
    /// </summary>
    private async Task _RefreshTriggerDescriptionAsync(IGlobalShortcutsPortal shortcuts, ObjectPath sessionHandle)
    {
        try
        {
            var bound = await _CallPortalRequestAsync(
                token => shortcuts.ListShortcutsAsync(sessionHandle, new Dictionary<string, object> { ["handle_token"] = token }),
                results => _TriggerFrom(results.TryGetValue("shortcuts", out var value) ? value : null)).ConfigureAwait(false);

            _SetTriggerDescription(bound);
        }
        catch (Exception exception)
        {
            // Not knowing what it was bound to is not a reason to leave the hotkey unarmed — the hold still
            // works, the settings screen just has nothing to report.
            logger.LogWarning(exception, "Could not read back what the compositor bound push-to-talk to.");
        }
    }

    private void _OnShortcutsChanged((ObjectPath Session, (string Id, IDictionary<string, object> Options)[] Shortcuts) changed) =>
        _SetTriggerDescription(_TriggerFromShortcuts(changed.Shortcuts));

    private void _SetTriggerDescription(string? description)
    {
        if (description == TriggerDescription)
        {
            return;
        }

        TriggerDescription = description;
        TriggerDescriptionChanged?.Invoke(this, EventArgs.Empty);
    }

    // "User-readable text describing how to trigger the shortcut for the client to render" — the spec's words,
    // and the whole reason this is displayed rather than the key the operator typed.
    private static string? _TriggerFromShortcuts((string Id, IDictionary<string, object> Options)[] shortcuts) =>
        shortcuts.FirstOrDefault(shortcut => shortcut.Id == ShortcutId).Options is { } options
        && options.TryGetValue("trigger_description", out var description)
            ? description as string
            : null;

    private static string? _TriggerFrom(object? shortcuts) =>
        shortcuts is (string, IDictionary<string, object>)[] typed ? _TriggerFromShortcuts(typed) : null;

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _activatedWatch?.Dispose();
        _deactivatedWatch?.Dispose();
        _shortcutsChangedWatch?.Dispose();
        _activatedWatch = null;
        _deactivatedWatch = null;
        _shortcutsChangedWatch = null;
        _connection?.Dispose();
        _connection = null;
        _SetTriggerDescription(null);
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
