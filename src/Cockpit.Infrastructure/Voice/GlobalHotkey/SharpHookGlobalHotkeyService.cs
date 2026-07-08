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

    public event EventHandler? HoldStarted;
    public event EventHandler? HoldEnded;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var settings = await voiceSettingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        _targetKey = _ParseKeyCode(settings.PushToTalkKeyName);
        if (_targetKey is null)
        {
            logger.LogWarning(
                "Push-to-talk key '{KeyName}' has no known SharpHook mapping; global push-to-talk will not fire.",
                settings.PushToTalkKeyName);
            return;
        }

        _hook.KeyPressed += _OnKeyPressed;
        _hook.KeyReleased += _OnKeyReleased;
        // Fire-and-forget: RunAsync's task only completes once the hook stops, and StartAsync itself
        // must return once the hook is armed, not block for the lifetime of the process.
        _ = _hook.RunAsync();
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _hook.KeyPressed -= _OnKeyPressed;
        _hook.KeyReleased -= _OnKeyReleased;
        _hook.Stop();
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
