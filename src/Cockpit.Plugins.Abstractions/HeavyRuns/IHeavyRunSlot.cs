namespace Cockpit.Plugins.Abstractions.HeavyRuns;

/// <summary>
/// A place in the machine-wide queue for heavy runs (AC-1535). Disposing it gives the place back — whether the run
/// finished, failed, or never got to start.
/// </summary>
public interface IHeavyRunSlot : IAsyncDisposable
{
    /// <summary>
    /// Completes once the run may start. Cancelled when the place is lost before that: the owning session went
    /// away, the operator removed it from the queue, or the slot was disposed while still waiting.
    /// </summary>
    Task Admitted { get; }

    /// <summary>
    /// Why the run is still waiting, in the operator's words ("waiting: CPU 85%, 2 ahead of you"); null once admitted.
    /// </summary>
    string? WaitReason { get; }

    /// <summary>
    /// Fires when an admitted run must stop: its owning session went away or the operator stopped it. Link the run's
    /// own cancellation to it so the process tree ends with the place.
    /// </summary>
    CancellationToken Revoked { get; }

    /// <summary>
    /// Hands the queue the output of a run that failed, so a failure that looks like the machine was overloaded
    /// (a database command timeout, an unhealthy container) can lower the admission ceiling.
    /// </summary>
    Task ReportFailureAsync(string output);
}
