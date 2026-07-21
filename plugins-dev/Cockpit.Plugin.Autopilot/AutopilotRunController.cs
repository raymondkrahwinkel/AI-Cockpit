namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The single place a started run lives between the trigger and the surface (AC-150). The "start" intent handler sets
/// <see cref="Current"/> through <see cref="Start"/>; the workspace body reads it and re-reads it on
/// <see cref="CurrentChanged"/>, so a run started while the Autopilot workspace is already open lands on it without a
/// rebuild. One controller is shared by the handler and the body — both are wired in the plugin's Initialize.
/// </summary>
internal sealed class AutopilotRunController
{
    public AutopilotRun? Current { get; private set; }

    public event EventHandler? CurrentChanged;

    public void Start(AutopilotRun run)
    {
        Current = run;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
    }
}
