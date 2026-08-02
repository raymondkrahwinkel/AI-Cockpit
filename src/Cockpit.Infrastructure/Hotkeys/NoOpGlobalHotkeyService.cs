using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Hotkeys;

namespace Cockpit.Infrastructure.Hotkeys;

// Fallback for platforms with neither the XDG GlobalShortcuts portal (Linux) nor a low-level keyboard hook
// (Windows, X11) wired up — macOS, where neither is available to us. Logs and never fires, so a cockpit build
// there still starts; the operator just has no desktop-wide keys (push-to-talk keeps its in-window key, and
// the screenshot its button).
internal sealed class NoOpGlobalHotkeyService(ILogger<NoOpGlobalHotkeyService> logger) : IGlobalHotkeyService
{
    // Explicit no-op accessors rather than field-like events: a field-like event that is never raised
    // triggers CS0067 ("event is never used"), which this class means literally by design.
    public event EventHandler<string>? Pressed { add { } remove { } }

    public event EventHandler<string>? Released { add { } remove { } }

    public event EventHandler? TriggerDescriptionsChanged { add { } remove { } }

    // Always null — nothing is armed. The settings screen says so, instead of showing a key that does nothing here.
    public string? TriggerDescriptionFor(string hotkeyId) => null;

    public Task StartAsync(IReadOnlyList<GlobalHotkeyBinding> bindings, CancellationToken cancellationToken = default)
    {
        if (bindings.Count > 0)
        {
            logger.LogInformation("Global hotkeys are not supported on this platform; {Count} binding(s) will not fire.", bindings.Count);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
