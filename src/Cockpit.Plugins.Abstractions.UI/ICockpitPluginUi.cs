namespace Cockpit.Plugins.Abstractions.UI;

// The UI entry point, activated where there is a window after the backend part's Initialize.
// Separate UI objects start IDisposable or IAsyncDisposable teardown on the UI thread without blocking its dispatcher.
// Live teardown awaits IAsyncDisposable before replacing or removing the plugin, then disposes the backend part.
public interface ICockpitPluginUi
{
    /// <summary>
    /// Registers everything the plugin shows — settings, menus, panels, header items — through
    /// <paramref name="host"/>. Runs once, on the UI thread, after the backend part's
    /// <see cref="ICockpitPlugin.Initialize"/> when the plugin has one.
    /// </summary>
    void InitializeUi(ICockpitUiHost host);
}
