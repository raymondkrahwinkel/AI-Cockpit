using Microsoft.Extensions.Logging;
using SharpHook;
using SharpHook.Data;
using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.Infrastructure.Voice.GlobalHotkey;

/// <summary>
/// Global push-to-talk via SharpHook's low-level keyboard hook (<c>WH_KEYBOARD_LL</c> on Windows) — the
/// Windows counterpart of <see cref="PortalGlobalHotkeyService"/>. Filters every raw key event down to
/// the configured <see cref="Cockpit.Core.Voice.VoiceSettings.PushToTalkKeyName"/> and reports its own
/// press/release edges as <see cref="HoldStarted"/>/<see cref="HoldEnded"/>; unlike Win32's
/// <c>RegisterHotKey</c> (press-only), the low-level hook sees both edges, which push-to-talk needs.
/// </summary>
internal sealed class SharpHookGlobalHotkeyService(IVoiceSettingsStore voiceSettingsStore, ILogger<SharpHookGlobalHotkeyService> logger)
    : IGlobalHotkeyService
{
    private readonly SimpleGlobalHook _hook = new(GlobalHookType.Keyboard);
    private KeyCode? _targetKey;
    private bool _isHolding;
    private bool _isRunning;

    public event EventHandler? HoldStarted;
    public event EventHandler? HoldEnded;
    public event EventHandler? TriggerDescriptionChanged;

    /// <summary>The key the hook is armed on. Windows binds what it is asked for, so this is the setting — once it has actually taken.</summary>
    public string? TriggerDescription { get; private set; }

    /// <summary>
    /// Arms the hook on the configured key. Safe to call again: it re-reads the setting and re-arms, which is
    /// how changing the key in Options takes effect without a restart.
    /// </summary>
    /// <remarks>
    /// It used to read the key exactly once, at startup, and nothing re-armed. Changing it in Options saved the
    /// new key and left the hook listening for the old one — with nothing anywhere to say so. The field looked
    /// like it decided the hotkey and did not.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var settings = await voiceSettingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var key = _ParseKeyCode(settings.PushToTalkKeyName);
        if (key is null)
        {
            logger.LogWarning(
                "Push-to-talk key '{KeyName}' has no known SharpHook mapping; global push-to-talk will not fire.",
                settings.PushToTalkKeyName);

            _targetKey = null;
            _SetTriggerDescription(null);
            return;
        }

        _targetKey = key;
        _isHolding = false;
        _SetTriggerDescription(settings.PushToTalkKeyName);

        if (_isRunning)
        {
            // The hook is already installed and reads _targetKey per event, so re-arming is the assignment above.
            // Running it twice would be a second hook on the same keyboard.
            return;
        }

        _hook.KeyPressed += _OnKeyPressed;
        _hook.KeyReleased += _OnKeyReleased;
        _isRunning = true;

        // Fire-and-forget: RunAsync's task only completes once the hook stops, and StartAsync itself
        // must return once the hook is armed, not block for the lifetime of the process.
        _ = _hook.RunAsync();
    }

    private void _SetTriggerDescription(string? description)
    {
        if (description == TriggerDescription)
        {
            return;
        }

        TriggerDescription = description;
        TriggerDescriptionChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _hook.KeyPressed -= _OnKeyPressed;
        _hook.KeyReleased -= _OnKeyReleased;
        _hook.Stop();
        _isRunning = false;
        _SetTriggerDescription(null);
        return Task.CompletedTask;
    }

    private void _OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        if (e.Data.KeyCode != _targetKey || _isHolding)
        {
            return;
        }

        _isHolding = true;
        HoldStarted?.Invoke(this, EventArgs.Empty);
    }

    private void _OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        if (e.Data.KeyCode != _targetKey || !_isHolding)
        {
            return;
        }

        _isHolding = false;
        HoldEnded?.Invoke(this, EventArgs.Empty);
    }

    // SharpHook's KeyCode enum mirrors libuiohook's naming ("Vc" + the key name), which lines up with
    // Avalonia's Key enum names for the simple function/alphanumeric keys this hotkey supports (e.g.
    // Avalonia's "F9" -> libuiohook's "VcF9") — good enough for the documented default and similarly
    // named keys; an exotic configured key name that has no "Vc"-prefixed match just logs and no-ops.
    private static KeyCode? _ParseKeyCode(string avaloniaKeyName) =>
        Enum.TryParse<KeyCode>("Vc" + avaloniaKeyName, ignoreCase: true, out var keyCode) ? keyCode : null;
}
