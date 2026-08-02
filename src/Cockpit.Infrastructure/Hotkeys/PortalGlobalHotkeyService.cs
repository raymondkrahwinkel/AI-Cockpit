using Microsoft.Extensions.Logging;
using Tmds.DBus;
using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Infrastructure.Portal;

namespace Cockpit.Infrastructure.Hotkeys;

// Global hotkeys via the XDG desktop portal's `org.freedesktop.portal.GlobalShortcuts` interface — the
// sandboxed-safe way for a desktop app to get system-wide keys on Wayland, where nothing can install a raw
// keyboard hook. Ported 1:1 from the working spike (`spike1_portal_hotkey.py`, live-confirmed on KDE
// Plasma 6.7/KWin): CreateSession, then BindShortcuts with a preferred-trigger hint, then listen for
// Activated/Deactivated on that session — Activated fires on physical key-down, Deactivated on key-up,
// exactly the hold semantics push-to-talk needs. The actual key bindings are owned by the compositor's own
// shortcut settings; the preferred trigger is only a hint the portal may or may not honour (KDE binds it
// directly).
// One session carries every binding. BindShortcuts has always taken an array, so the second hotkey (the
// screenshot capture, AC-220) costs nothing here — and, more to the point, does not show up as a second
// application in the operator's shortcut settings.
internal sealed class PortalGlobalHotkeyService(ILogger<PortalGlobalHotkeyService> logger) : IGlobalHotkeyService
{
    private const string BusName = "org.freedesktop.portal.Desktop";
    private static readonly ObjectPath DesktopPath = new("/org/freedesktop/portal/desktop");

    private Connection? _connection;
    private PortalRequestChannel? _requests;
    private IDisposable? _activatedWatch;
    private IDisposable? _deactivatedWatch;
    private IDisposable? _shortcutsChangedWatch;
    private IReadOnlyDictionary<string, string> _triggerDescriptions = new Dictionary<string, string>();

    // Which hotkeys are down, so a hold collapses to one edge whatever the desktop repeats.
    // Behind a lock for the reason `SharpHookGlobalHotkeyService`'s is: it is written from the D-Bus main
    // loop on every Activated/Deactivated and cleared from the caller's thread on every arm. A hold whose
    // key-up finds nothing to remove is push-to-talk never hearing the release, with the microphone still open.
    private readonly HashSet<string> _held = [];

    // Guards the two pieces of state the D-Bus loop and the caller's thread both reach: `_held` and the published trigger descriptions.
    private readonly Lock _stateGate = new();

    public event EventHandler<string>? Pressed;
    public event EventHandler<string>? Released;
    public event EventHandler? TriggerDescriptionsChanged;

    // What the compositor bound, in its own words. Empty until `StartAsync` has asked it.
    public string? TriggerDescriptionFor(string hotkeyId) => _triggerDescriptions.GetValueOrDefault(hotkeyId);

    public async Task StartAsync(IReadOnlyList<GlobalHotkeyBinding> bindings, CancellationToken cancellationToken = default)
    {
        // Registering a different set means a different session: the portal binds at CreateSession time and
        // there is no "rebind" that keeps the old session honest.
        await StopAsync(cancellationToken).ConfigureAwait(false);

        if (bindings.Count == 0)
        {
            return;
        }

        _connection = new Connection(Address.Session);
        _requests = await PortalRequestChannel.ConnectAsync(_connection).ConfigureAwait(false);

        var shortcuts = _connection.CreateProxy<IGlobalShortcutsPortal>(BusName, DesktopPath);

        var sessionHandle = await _CallPortalRequestAsync(
            token => shortcuts.CreateSessionAsync(new Dictionary<string, object>
            {
                ["handle_token"] = token,
                ["session_handle_token"] = _requests.NextToken("sess"),
            }),
            // The GlobalShortcuts portal returns session_handle as a plain string ('s'), not an object
            // path ('o') — a long-standing quirk of this portal interface — so wrap it rather than cast.
            results => new ObjectPath((string)results["session_handle"])).ConfigureAwait(false);

        await _CallPortalRequestAsync(
            token => shortcuts.BindShortcutsAsync(
                sessionHandle,
                [.. bindings.Select(binding => (binding.Id, (IDictionary<string, object>)new Dictionary<string, object>
                {
                    ["description"] = binding.Description,
                    // A hint, and the spec says so: the compositor may bind something else, or leave it to the
                    // operator. Which is why the answer is asked for below rather than assumed from this.
                    ["preferred_trigger"] = binding.KeyName,
                }))],
                string.Empty,
                new Dictionary<string, object> { ["handle_token"] = token }),
            static _ => true).ConfigureAwait(false);

        _activatedWatch = await shortcuts.WatchActivatedAsync(_OnActivated).ConfigureAwait(false);
        _deactivatedWatch = await shortcuts.WatchDeactivatedAsync(_OnDeactivated).ConfigureAwait(false);
        _shortcutsChangedWatch = await shortcuts.WatchShortcutsChangedAsync(_OnShortcutsChanged).ConfigureAwait(false);

        await _RefreshTriggerDescriptionsAsync(shortcuts, sessionHandle).ConfigureAwait(false);

        foreach (var binding in bindings)
        {
            logger.LogInformation(
                "Global hotkey {HotkeyId} registered via the XDG GlobalShortcuts portal; asked for '{Preferred}', bound to '{Bound}'.",
                binding.Id,
                binding.KeyName,
                TriggerDescriptionFor(binding.Id) ?? "<nothing yet — bind it in your desktop's shortcut settings>");
        }
    }

    // Asks the compositor what it bound. This is the only place that answer exists: the preferred trigger is a
    // hint, and on a desktop that leaves the binding to its own shortcut settings the honest answer is that
    // nothing is bound until the operator does it — which is a thing to say, not to guess at.
    private async Task _RefreshTriggerDescriptionsAsync(IGlobalShortcutsPortal shortcuts, ObjectPath sessionHandle)
    {
        try
        {
            var bound = await _CallPortalRequestAsync(
                token => shortcuts.ListShortcutsAsync(sessionHandle, new Dictionary<string, object> { ["handle_token"] = token }),
                results => _TriggersFrom(results.TryGetValue("shortcuts", out var value) ? value : null)).ConfigureAwait(false);

            _SetTriggerDescriptions(bound);
        }
        catch (Exception exception)
        {
            // Not knowing what they were bound to is not a reason to leave the hotkeys unarmed — the keys still
            // work, the settings screen just has nothing to report.
            logger.LogWarning(exception, "Could not read back what the compositor bound the cockpit's hotkeys to.");
        }
    }

    private void _OnShortcutsChanged((ObjectPath Session, (string Id, IDictionary<string, object> Options)[] Shortcuts) changed) =>
        _SetTriggerDescriptions(_TriggersFromShortcuts(changed.Shortcuts));

    // Publishes what the compositor says it bound, and reports it only when it actually moved.
    // Under the same lock as the held set, because this is reached from both threads too: the desktop can send
    // a ShortcutsChanged at any moment on the D-Bus loop while an arm is clearing it from the caller's. The
    // dictionary itself is built fresh and swapped, so nothing can tear — what needs the lock is the
    // compare-then-swap, or a rebind the operator just made can be dropped as "unchanged" against a value that
    // was already on its way out.
    private void _SetTriggerDescriptions(IReadOnlyDictionary<string, string> descriptions)
    {
        lock (_stateGate)
        {
            var unchanged = descriptions.Count == _triggerDescriptions.Count
                && descriptions.All(entry => _triggerDescriptions.TryGetValue(entry.Key, out var existing) && existing == entry.Value);

            if (unchanged)
            {
                return;
            }

            _triggerDescriptions = descriptions;
        }

        // Outside the lock, like the key events: what a subscriber does with this reaches the UI thread.
        TriggerDescriptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    // "User-readable text describing how to trigger the shortcut for the client to render" — the spec's words,
    // and the whole reason this is displayed rather than the key the operator typed.
    private static IReadOnlyDictionary<string, string> _TriggersFromShortcuts((string Id, IDictionary<string, object> Options)[] shortcuts)
    {
        var descriptions = new Dictionary<string, string>();
        foreach (var (id, options) in shortcuts)
        {
            if (options.TryGetValue("trigger_description", out var description) && description is string text)
            {
                descriptions[id] = text;
            }
        }

        return descriptions;
    }

    private static IReadOnlyDictionary<string, string> _TriggersFrom(object? shortcuts) =>
        shortcuts is (string, IDictionary<string, object>)[] typed
            ? _TriggersFromShortcuts(typed)
            : new Dictionary<string, string>();

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
        _requests = null;
        lock (_stateGate)
        {
            _held.Clear();
        }
        _SetTriggerDescriptions(new Dictionary<string, string>());
        return Task.CompletedTask;
    }

    // Gated on the held set for the same reason the keyboard hook is: a hold must collapse to one edge, or
    // push-to-talk restarts its recording on every repeat the desktop sends. The subscriber is called outside
    // the lock — what it goes on to do is not work to hold a lock through.
    private void _OnActivated((ObjectPath Session, string ShortcutId, ulong Timestamp, IDictionary<string, object> Options) activation)
    {
        bool wentDown;
        lock (_stateGate)
        {
            wentDown = _held.Add(activation.ShortcutId);
        }

        if (wentDown)
        {
            Pressed?.Invoke(this, activation.ShortcutId);
        }
    }

    private void _OnDeactivated((ObjectPath Session, string ShortcutId, ulong Timestamp, IDictionary<string, object> Options) deactivation)
    {
        bool cameUp;
        lock (_stateGate)
        {
            cameUp = _held.Remove(deactivation.ShortcutId);
        }

        if (cameUp)
        {
            Released?.Invoke(this, deactivation.ShortcutId);
        }
    }

    // The two-step portal call itself lives in PortalRequestChannel, shared with the screenshot capture
    // (AC-220). What is this service's own is what a non-success code means: arming a hotkey has no
    // "the operator changed their mind" — anything but success is a key that will not fire, and saying so is
    // what lets the caller log it rather than leave a dead key nobody was told about.
    private async Task<T> _CallPortalRequestAsync<T>(
        Func<string, Task<ObjectPath>> invoke,
        Func<IDictionary<string, object>, T> project)
    {
        var requests = _requests ?? throw new InvalidOperationException($"{nameof(PortalGlobalHotkeyService)} is not connected.");

        var response = await requests.InvokeAsync(invoke).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            throw new InvalidOperationException($"Portal request failed with response code {response.ResponseCode}.");
        }

        return project(response.Results);
    }
}
