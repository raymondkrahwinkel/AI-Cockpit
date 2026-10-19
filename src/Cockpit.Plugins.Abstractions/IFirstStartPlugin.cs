namespace Cockpit.Plugins.Abstractions;

// AC-1509: the backend half of a plugin's first-start route. Implemented by the plugin's entry type beside ICockpitPlugin.
public interface IFirstStartPlugin
{
    /// <summary>
    /// Registers what the plugin's first-start route needs from its backend part — channel handlers, typically — before
    /// the first-run wizard opens and before <see cref="ICockpitPlugin.Initialize"/> has run. Runs once per start, on the
    /// UI thread, whether or not the wizard is shown. <see cref="ICockpitPlugin.Initialize"/> still runs later, unchanged,
    /// with a host of its own.
    /// </summary>
    void InitializeFirstStart(ICockpitHost host);
}
