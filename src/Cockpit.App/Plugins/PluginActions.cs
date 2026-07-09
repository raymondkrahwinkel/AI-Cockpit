using Avalonia.Input.Platform;
using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

/// <summary>
/// <see cref="ICockpitActions"/> a plugin uses to act on the cockpit: inject text into the selected
/// session (reusing the session's own per-kind input seam) and put text on the clipboard. The clipboard
/// is resolved lazily via a factory so this has no hard dependency on a window being up.
/// </summary>
public sealed class PluginActions(CockpitViewModel cockpit, Func<IClipboard?> clipboardFactory) : ICockpitActions
{
    public bool HasActiveSession => cockpit.SelectedSession is not null;

    public Task InjectIntoActiveSessionAsync(string text)
    {
        cockpit.SelectedSession?.InjectText(text);
        return Task.CompletedTask;
    }

    public async Task SetClipboardTextAsync(string text)
    {
        if (clipboardFactory() is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
