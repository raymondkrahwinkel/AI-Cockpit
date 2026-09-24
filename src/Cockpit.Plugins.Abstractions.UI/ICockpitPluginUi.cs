namespace Cockpit.Plugins.Abstractions.UI;

// AC-1389: the entry point of a plugin's UI part, named by the manifest's uiEntryType, or implemented by the
// ICockpitPlugin entry type itself while a plugin moves its UI registrations over before it is split in two.
// Activated only where there is a window, after the backend part's Initialize; disposed with the plugin when it
// is IDisposable.
public interface ICockpitPluginUi
{
    /// <summary>
    /// Registers everything the plugin shows — settings, menus, panels, header items — through
    /// <paramref name="host"/>. Runs once, on the UI thread, after the backend part's
    /// <see cref="ICockpitPlugin.Initialize"/> when the plugin has one.
    /// </summary>
    void InitializeUi(ICockpitUiHost host);
}
