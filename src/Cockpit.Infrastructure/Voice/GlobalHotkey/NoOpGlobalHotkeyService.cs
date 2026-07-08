using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.Infrastructure.Voice.GlobalHotkey;

/// <summary>
/// Fallback for platforms with neither the XDG GlobalShortcuts portal (Linux) nor a low-level keyboard
/// hook (Windows) wired up — e.g. macOS, not yet supported. Logs and never fires, so a cockpit build on
/// an unsupported OS still starts; the operator just can't use the global hotkey there (the per-view
/// local F9 keeps working, since <see cref="Cockpit.Core.Voice.VoiceSettings.GlobalPushToTalk"/> only
/// gates it off when global push-to-talk actually started).
/// </summary>
internal sealed class NoOpGlobalHotkeyService(ILogger<NoOpGlobalHotkeyService> logger) : IGlobalHotkeyService
{
    // Explicit no-op accessors rather than a field-like event: a field-like event that is never raised
    // triggers CS0067 ("event is never used"), which this class means literally by design.
    public event EventHandler? HoldStarted { add { } remove { } }

    public event EventHandler? HoldEnded { add { } remove { } }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Global push-to-talk is not supported on this platform; the hotkey will not fire.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
