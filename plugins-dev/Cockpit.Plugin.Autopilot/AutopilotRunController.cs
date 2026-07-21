namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The single place a run's state lives between the trigger, the scoping judgment and the surface (AC-150/AC-151). The
/// "start" intent handler drives it through <see cref="BeginScoping"/> → <see cref="Refuse"/> or <see cref="MarkRunning"/>;
/// the workspace body reads <see cref="Current"/>/<see cref="Phase"/> and re-reads them on <see cref="Changed"/>, so a
/// run that advances while the Autopilot workspace is already open lands on it without a rebuild. One controller is
/// shared by the handler and the body — both are wired in the plugin's Initialize.
/// </summary>
internal sealed class AutopilotRunController
{
    public AutopilotRun? Current { get; private set; }

    public AutopilotRunPhase Phase { get; private set; }

    /// <summary>Why the current run was refused, when <see cref="Phase"/> is <see cref="AutopilotRunPhase.Refused"/>; otherwise null.</summary>
    public string? RefusalReason { get; private set; }

    public event EventHandler? Changed;

    /// <summary>Whether <paramref name="run"/> is still the current run — false once a newer point replaced it, so a slow scoping verdict that arrives late does not flip the run that took over.</summary>
    public bool IsCurrent(AutopilotRun run) => ReferenceEquals(Current, run);

    /// <summary>A new point arrived: it becomes the current run, being scoped, replacing any earlier one.</summary>
    public void BeginScoping(AutopilotRun run)
    {
        Current = run;
        Phase = AutopilotRunPhase.Scoping;
        RefusalReason = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Scoping refused the point: it parks with <paramref name="reason"/> rather than starting a session.</summary>
    public void Refuse(string reason)
    {
        Phase = AutopilotRunPhase.Refused;
        RefusalReason = reason;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Scoping passed: the run advances to running, the signal the body embeds its session on.</summary>
    public void MarkRunning()
    {
        Phase = AutopilotRunPhase.Running;
        RefusalReason = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
