namespace Cockpit.Plugin.LocalCi.Runtime;

/// <summary>
/// The one place that knows what this machine can run. Everything downstream — the settings line, and later the
/// part that actually starts a job — asks here rather than probing again, so there is a single answer and a single
/// moment it was taken.
/// </summary>
internal interface ILocalCiRuntime
{
    /// <summary>
    /// The current answer, probing once and caching it. Concurrent callers share one probe rather than racing two.
    /// </summary>
    Task<LocalCiRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops the cached answer so the next <see cref="GetStatusAsync"/> probes again.
    /// </summary>
    void Invalidate();
}

// Both halves of the answer: the engine that runs the work, and the runtime that drives it.
internal sealed record LocalCiRuntimeStatus(DockerRuntimeStatus Docker, ActRuntimeStatus Act)
{
    // True when a workflow job could actually be attempted here.
    public bool CanRunJobs => Docker.IsReady && Act.IsInstalled;
}
