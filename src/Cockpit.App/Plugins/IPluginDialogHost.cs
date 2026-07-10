using Avalonia.Controls;

namespace Cockpit.App.Plugins;

/// <summary>Opens a modal dialog over the main window hosting a plugin-built control (#14) — backs <c>ICockpitHost.ShowDialogAsync</c> and the plugin-settings gear.</summary>
public interface IPluginDialogHost
{
    Task ShowDialogAsync(string title, Func<Control> createContent, double width, double height);

    /// <summary>Opens a plugin's settings view with a host-provided Save/Close footer; Save calls the view's <c>IPluginSettingsView.Save()</c> and closes on success.</summary>
    Task ShowSettingsDialogAsync(string title, Func<Control> createView, double width, double height);
}
