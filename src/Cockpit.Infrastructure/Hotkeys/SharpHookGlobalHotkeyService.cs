using Microsoft.Extensions.Logging;
using SharpHook;
using SharpHook.Data;
using Cockpit.Core.Abstractions.Hotkeys;

namespace Cockpit.Infrastructure.Hotkeys;

// Global hotkeys via SharpHook's low-level keyboard hook (Windows, X11) — the counterpart of
// `PortalGlobalHotkeyService`. Unlike Win32's `RegisterHotKey` (press-only), the low-level hook sees
// both press/release edges, which push-to-talk's hold needs. One hook serves every binding; re-arming is just an assignment, not a second hook.
internal sealed class SharpHookGlobalHotkeyService(ILogger<SharpHookGlobalHotkeyService> logger) : IGlobalHotkeyService
{
    private readonly SimpleGlobalHook _hook = new(GlobalHookType.Keyboard);

    // The armed keys by hotkey id, replaced wholesale on each `StartAsync`. Read on the hook's own thread, so it is swapped rather than mutated.
    private IReadOnlyDictionary<KeyCode, GlobalHotkeyBinding> _armed = new Dictionary<KeyCode, GlobalHotkeyBinding>();

    private IReadOnlyDictionary<string, string> _triggerDescriptions = new Dictionary<string, string>();

    // Which hotkeys are down, so a hold collapses to one edge whatever the OS repeats. Behind a lock:
    // written from the hook's own thread on every key event and cleared from the caller's thread on every
    // arm. Unlike a single bool, a set write can tear, and a torn write leaves a key-up finding nothing to remove.
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
    // changing a key in Options takes effect without a restart (previously the key was read once at
    // startup and never re-armed, silently leaving the hook listening for the old key).
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

    // The OS repeats a held key, so a hold has to collapse to one edge: reported as pressed only when not
    // already down. Push-to-talk depends on this — a repeat would restart the recording. The subscriber is
    // called outside the lock, since a lock held across a hand-off to other people's code is how a deadlock starts.
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

    // SharpHook's KeyCode mirrors libuiohook's naming ("Vc" + key name), matching Avalonia's Key names
    // for simple function/alphanumeric keys (e.g. "F9" -> "VcF9") — good enough for documented defaults;
    // an exotic key name with no "Vc"-prefixed match just logs and no-ops.
    private static KeyCode? _ParseKeyCode(string avaloniaKeyName) =>
        Enum.TryParse<KeyCode>("Vc" + avaloniaKeyName, ignoreCase: true, out var keyCode) ? keyCode : null;
}
