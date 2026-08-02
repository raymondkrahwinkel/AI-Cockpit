using Microsoft.Extensions.Logging;
using SharpHook;
using SharpHook.Data;
using Cockpit.Core.Abstractions.Hotkeys;

namespace Cockpit.Infrastructure.Hotkeys;

// Global hotkeys via SharpHook's low-level keyboard hook (`WH_KEYBOARD_LL` on Windows, X11's input on
// Linux) — the counterpart of `PortalGlobalHotkeyService`. Filters every raw key event down to
// the registered keys and reports their press/release edges; unlike Win32's `RegisterHotKey`
// (press-only), the low-level hook sees both edges, which push-to-talk's hold needs.
// One hook serves every binding: it is installed once and reads the current key map per event, so re-arming
// on a changed key is an assignment rather than a second hook on the same keyboard.
internal sealed class SharpHookGlobalHotkeyService(ILogger<SharpHookGlobalHotkeyService> logger) : IGlobalHotkeyService
{
    private readonly SimpleGlobalHook _hook = new(GlobalHookType.Keyboard);

    // The armed keys by hotkey id, replaced wholesale on each `StartAsync`. Read on the hook's own thread, so it is swapped rather than mutated.
    private IReadOnlyDictionary<KeyCode, GlobalHotkeyBinding> _armed = new Dictionary<KeyCode, GlobalHotkeyBinding>();

    private IReadOnlyDictionary<string, string> _triggerDescriptions = new Dictionary<string, string>();

    // Which hotkeys are down, so a hold collapses to one edge whatever the OS repeats.
    // Behind a lock, and not out of habit: it is written from the hook's own thread on every key event and
    // cleared from the caller's thread on every arm. While this was a single `bool` — one key, one hold —
    // that pairing was harmless, because a bool write cannot tear. A set can, and the failure it buys is a hold
    // whose key-up finds nothing to remove: push-to-talk never hears the release and the microphone stays open.
    private readonly HashSet<string> _held = [];
    private readonly Lock _heldGate = new();

    private bool _isRunning;

    public event EventHandler<string>? Pressed;
    public event EventHandler<string>? Released;
    public event EventHandler? TriggerDescriptionsChanged;

    // Windows and X11 bind what they are asked for, so this is the configured key — once it has actually taken.
    public string? TriggerDescriptionFor(string hotkeyId) =>
        _triggerDescriptions.GetValueOrDefault(hotkeyId);

    // Arms the hook on exactly the given keys. Safe to call again: it replaces the key map, which is how
    // changing a key in Options takes effect without a restart.
    // The key used to be read exactly once, at startup, and nothing re-armed. Changing it in Options saved the
    // new key and left the hook listening for the old one — with nothing anywhere to say so.
    public Task StartAsync(IReadOnlyList<GlobalHotkeyBinding> bindings, CancellationToken cancellationToken = default)
    {
        var armed = new Dictionary<KeyCode, GlobalHotkeyBinding>();
        var descriptions = new Dictionary<string, string>();

        foreach (var binding in bindings)
        {
            if (_ParseKeyCode(binding.KeyName) is not { } key)
            {
                logger.LogWarning(
                    "Hotkey '{KeyName}' for {HotkeyId} has no known SharpHook mapping; it will not fire.",
                    binding.KeyName,
                    binding.Id);

                continue;
            }

            // Two features on one key would make every press ambiguous. First registered wins and the clash is
            // said out loud, rather than one of them quietly never firing.
            if (!armed.TryAdd(key, binding))
            {
                logger.LogWarning(
                    "Hotkey '{KeyName}' is already taken by {ExistingHotkeyId}; {HotkeyId} will not fire until one of them is changed.",
                    binding.KeyName,
                    armed[key].Id,
                    binding.Id);

                continue;
            }

            descriptions[binding.Id] = binding.KeyName;
        }

        _armed = armed;
        lock (_heldGate)
        {
            _held.Clear();
        }
        _SetTriggerDescriptions(descriptions);

        if (_isRunning || armed.Count == 0)
        {
            return Task.CompletedTask;
        }

        _hook.KeyPressed += _OnKeyPressed;
        _hook.KeyReleased += _OnKeyReleased;
        _isRunning = true;

        // Fire-and-forget: RunAsync's task only completes once the hook stops, and StartAsync itself must
        // return once the hook is armed, not block for the lifetime of the process.
        _ = _hook.RunAsync();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _hook.KeyPressed -= _OnKeyPressed;
        _hook.KeyReleased -= _OnKeyReleased;
        _hook.Stop();
        _isRunning = false;
        _armed = new Dictionary<KeyCode, GlobalHotkeyBinding>();
        lock (_heldGate)
        {
            _held.Clear();
        }
        _SetTriggerDescriptions(new Dictionary<string, string>());
        return Task.CompletedTask;
    }

    private void _SetTriggerDescriptions(IReadOnlyDictionary<string, string> descriptions)
    {
        var unchanged = descriptions.Count == _triggerDescriptions.Count
            && descriptions.All(entry => _triggerDescriptions.TryGetValue(entry.Key, out var existing) && existing == entry.Value);

        if (unchanged)
        {
            return;
        }

        _triggerDescriptions = descriptions;
        TriggerDescriptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    // The OS repeats a held key, so a hold has to collapse to one edge: the id is only reported as pressed
    // when it was not already down. Push-to-talk depends on this — a repeat would restart the recording.
    //
    // The subscriber is called outside the lock. Everything it goes on to do — marshalling to the UI thread,
    // opening a microphone — is not work to hold a lock through, and a lock held across a hand-off to other
    // people's code is how a deadlock starts.
    private void _OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        if (!_armed.TryGetValue(e.Data.KeyCode, out var binding))
        {
            return;
        }

        bool wentDown;
        lock (_heldGate)
        {
            wentDown = _held.Add(binding.Id);
        }

        if (wentDown)
        {
            Pressed?.Invoke(this, binding.Id);
        }
    }

    private void _OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        if (!_armed.TryGetValue(e.Data.KeyCode, out var binding))
        {
            return;
        }

        bool cameUp;
        lock (_heldGate)
        {
            cameUp = _held.Remove(binding.Id);
        }

        if (cameUp)
        {
            Released?.Invoke(this, binding.Id);
        }
    }

    // SharpHook's KeyCode enum mirrors libuiohook's naming ("Vc" + the key name), which lines up with
    // Avalonia's Key enum names for the simple function/alphanumeric keys these hotkeys support (e.g.
    // Avalonia's "F9" -> libuiohook's "VcF9") — good enough for the documented defaults and similarly
    // named keys; an exotic configured key name that has no "Vc"-prefixed match just logs and no-ops.
    private static KeyCode? _ParseKeyCode(string avaloniaKeyName) =>
        Enum.TryParse<KeyCode>("Vc" + avaloniaKeyName, ignoreCase: true, out var keyCode) ? keyCode : null;
}
