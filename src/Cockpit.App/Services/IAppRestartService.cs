namespace Cockpit.App.Services;

/// <summary>
/// Restarts the running app for the operator (#53), for the "restart to apply" moments the plugin manager
/// surfaces after install/enable/disable/remove — those stay a real restart (a loaded plugin assembly cannot
/// be unloaded/loaded live), but the operator no longer has to close and relaunch the app by hand.
/// </summary>
public interface IAppRestartService
{
    /// <summary>Launches a fresh instance of the app, then cleanly shuts this one down through its existing exit path.</summary>
    void Restart();
}
