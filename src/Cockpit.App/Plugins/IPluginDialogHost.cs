using Avalonia.Controls;

namespace Cockpit.App.Plugins;

/// <summary>
/// Opens a window beside the cockpit hosting a plugin-built control (#14) — backs <c>ICockpitHost.ShowDialogAsync</c> and the plugin-settings gear. Not modal (AC-367): every running session stays reachable while it is open. Without a <c>singleInstanceKey</c> each call opens its own window and builds its content afresh, because a caption is not enough to tell two of them apart — only the plugin knows whether two of its windows are the same thing.
/// </summary>
public interface IPluginDialogHost
{
    /// <summary>
    /// Opens a plugin's dialog. <paramref name="onOpenSettings"/>, when given, puts a settings gear in the title bar.
    /// <paramref name="singleInstanceKey"/>, when given, reduces this window to one: asked for again while open, that
    /// window comes forward and <paramref name="createContent"/> is not run.
    /// </summary>
    Task ShowDialogAsync(string title, Func<Control> createContent, double width, double height, Func<Task>? onOpenSettings = null, string? singleInstanceKey = null);

    /// <summary>
    /// Opens a plugin's settings view with a host-provided Save/Close footer; Save stages the view's write, performs
    /// it, closes, then runs <paramref name="onSaved"/> — always after, never without it (#52, AC-1004). A refused
    /// save keeps the window open with the view's own reason.
    /// </summary>
    Task ShowSettingsDialogAsync(string title, Func<Control> createView, double width, double height, Action? onSaved = null, string? singleInstanceKey = null);
}
