namespace Cockpit.Core.HeavyRuns;

/// <summary>
/// The machine's measured load (AC-1535): CPU and memory of the whole machine, not of the cockpit's own process,
/// smoothed over a short window. The one monitor the heavy-run queue, disk forecasting and pressure levels read.
/// </summary>
public interface IMachineResources
{
    /// <summary>
    /// The smoothed load as of the last sample; <see cref="MachineLoad.Unknown"/> before the first.
    /// </summary>
    MachineLoad Current { get; }

    /// <summary>Shared pressure samples keep admission and maintenance on the same measurements.</summary>
    IReadOnlyList<DiskSpaceSnapshot> Disks => [];

    /// <summary>
    /// Free bytes on the volume that holds <paramref name="directory"/>, or null when that cannot be read.
    /// </summary>
    long? FreeDiskBytes(string directory);

    /// <summary>
    /// Raised after every sample, on a background thread — the queue's cue to look at its waiting runs again.
    /// </summary>
    event Action? Sampled;
}
