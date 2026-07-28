using Avalonia.Controls;

namespace Cockpit.App.Plugins;

/// <summary>Opens a window beside the cockpit hosting a plugin-built control (#14) — backs <c>ICockpitHost.ShowDialogAsync</c> and the plugin-settings gear. Not modal (AC-367): every running session stays reachable while it is open. Without a <c>singleInstanceKey</c> each call opens its own window and builds its content afresh, because a caption is not enough to tell two of them apart — only the plugin knows whether two of its windows are the same thing.</summary>
public interface IPluginDialogHost
{
    /// <summary>
    /// Opens a plugin's dialog. <paramref name="onOpenSettings"/>, when given, puts a gear in the dialog's title
    /// bar that runs it — how a plugin's own settings are reached from the dialog that needed them, instead of
    /// sending the operator off to the plugin manager.
    /// <para>
    /// <paramref name="singleInstanceKey"/>, when given, reduces this window to one: asked for again while it is
    /// open, that window comes forward and <paramref name="createContent"/> is not run. Already scoped to the
    /// plugin by the caller.
    /// </para>
    /// </summary>
    Task ShowDialogAsync(string title, Func<Control> createContent, double width, double height, Func<Task>? onOpenSettings = null, string? singleInstanceKey = null);

    /// <summary>
    /// Opens a plugin's settings view with a host-provided Save/Close footer; Save calls the view's
    /// <c>IPluginSettingsView.Save()</c> and closes on success, running <paramref name="onSaved"/> first
    /// (#52) so the caller can trigger that plugin's <c>ICockpitHost.OnSettingsSaved</c> subscribers.
    /// </summary>
    Task ShowSettingsDialogAsync(string title, Func<Control> createView, double width, double height, Action? onSaved = null, string? singleInstanceKey = null);
}
