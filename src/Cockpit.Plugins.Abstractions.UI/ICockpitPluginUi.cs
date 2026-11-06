namespace Cockpit.Plugins.Abstractions.UI;

// AC-1389: the UI part's entry point, the manifest's uiEntryType or the ICockpitPlugin entry type itself. Activated only
// where there is a window, after the backend part's Initialize. AC-2024: when IDisposable, it is disposed on the UI
// thread, before the backend part: stop, never wait there, since the host cannot interrupt the call.
public interface ICockpitPluginUi
{
    /// <summary>
    /// Registers everything the plugin shows — settings, menus, panels, header items — through
    /// <paramref name="host"/>. Runs once, on the UI thread, after the backend part's
    /// <see cref="ICockpitPlugin.Initialize"/> when the plugin has one.
    /// </summary>
    void InitializeUi(ICockpitUiHost host);
}
