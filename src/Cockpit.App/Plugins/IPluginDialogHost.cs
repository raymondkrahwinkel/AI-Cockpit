using Avalonia.Controls;

namespace Cockpit.App.Plugins;

/// <summary>Opens a modal dialog over the main window hosting a plugin-built control (#14) — backs <c>ICockpitHost.ShowDialogAsync</c> and the plugin-settings gear.</summary>
public interface IPluginDialogHost
{
    /// <summary>
    /// Opens a plugin's dialog. <paramref name="onOpenSettings"/>, when given, puts a gear in the dialog's title
    /// bar that runs it — how a plugin's own settings are reached from the dialog that needed them, instead of
    /// sending the operator off to the plugin manager.
    /// </summary>
    Task ShowDialogAsync(string title, Func<Control> createContent, double width, double height, Func<Task>? onOpenSettings = null);

    /// <summary>
    /// Opens a plugin's settings view with a host-provided Save/Close footer; Save calls the view's
    /// <c>IPluginSettingsView.Save()</c> and closes on success, running <paramref name="onSaved"/> first
    /// (#52) so the caller can trigger that plugin's <c>ICockpitHost.OnSettingsSaved</c> subscribers.
    /// </summary>
    Task ShowSettingsDialogAsync(string title, Func<Control> createView, double width, double height, Action? onSaved = null);
}
