namespace Cockpit.Plugin.SystemMonitor;

/// <summary>
/// The three readings the System Monitor shows, from what .NET exposes on every platform the cockpit runs on.
/// Deliberately shallow: a widget is a glance, not a profiler, so this reads what is cheap and honest rather
/// than reaching for per-platform counters.
/// </summary>
/// <remarks>
/// An instance rather than a static, because <see cref="CpuPercent"/> is a difference against the previous
/// reading and that sample point belongs to the pane doing the asking. Shared statically, two System Monitors —
/// and placing one twice, configured differently, is the whole point of a widget with a ⚙ — consumed each
/// other's: both timers fire two seconds apart, so whichever ran second measured against a sample point
/// microseconds old and reported a number that was not the CPU share at all. Nothing is shared now, so there is
/// nothing to corrupt.
/// </remarks>
internal sealed class SystemUsage
{
    private TimeSpan _lastCpuTime = TimeSpan.Zero;
    private DateTimeOffset _lastSampledAt = DateTimeOffset.MinValue;

    /// <summary>
    /// The cockpit's own CPU share since this instance's previous call, across all cores. Process-scoped rather
    /// than machine-wide: a cross-platform machine-wide figure needs per-OS counters, and what this pane is for —
    /// "is the thing I am running busy" — is answered by the process. The first call has no interval to measure
    /// against and reports zero.
    /// </summary>
    /// <remarks>
    /// <see cref="Environment.CpuUsage"/> rather than <c>Process.GetCurrentProcess().TotalProcessorTime</c>: the
    /// same measurement (user + privileged time for this process), but it holds no native handle. The old form
    /// built a new undisposed <c>Process</c> around one on every reading — thirty a minute per pane, all waiting
    /// on the finalizer.
    /// </remarks>
    public double CpuPercent()
    {
        var now = DateTimeOffset.UtcNow;
        var cpu = Environment.CpuUsage.TotalTime;

        if (_lastSampledAt == DateTimeOffset.MinValue)
        {
            (_lastCpuTime, _lastSampledAt) = (cpu, now);
            return 0;
        }

        var elapsed = (now - _lastSampledAt).TotalMilliseconds;
        var used = (cpu - _lastCpuTime).TotalMilliseconds;
        (_lastCpuTime, _lastSampledAt) = (cpu, now);

        return elapsed <= 0 ? 0 : Math.Clamp(used / (elapsed * Environment.ProcessorCount) * 100, 0, 100);
    }

    /// <summary>How much of the machine's memory is in use, from the GC's view of the host.</summary>
    public double MemoryPercent()
    {
        var info = GC.GetGCMemoryInfo();
        return info.TotalAvailableMemoryBytes <= 0
            ? 0
            : Math.Clamp((double)info.MemoryLoadBytes / info.TotalAvailableMemoryBytes * 100, 0, 100);
    }

    /// <summary>How full the drive the cockpit runs from is.</summary>
    public double DiskPercent()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(AppContext.BaseDirectory) ?? "/");
            return drive.TotalSize <= 0
                ? 0
                : Math.Clamp((double)(drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize * 100, 0, 100);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An unreadable drive is a reason for the bar to read zero, not for the dashboard to fall over.
            return 0;
        }
    }
}
