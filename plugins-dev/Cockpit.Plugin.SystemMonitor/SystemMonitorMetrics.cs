namespace Cockpit.Plugin.SystemMonitor;

/// <summary>
/// Which of the three readings a System Monitor instance shows — the widget's whole configuration, and the
/// reason it has a settings form at all. Stored per instance, so two monitors on one dashboard can show
/// different things.
/// </summary>
/// <remarks>
/// A record rather than three loose storage keys: it round-trips through <c>IPluginStorage</c> as one JSON
/// value, so a partially-written config cannot leave the widget showing nothing.
/// </remarks>
internal sealed record SystemMonitorMetrics
{
    public bool ShowCpu { get; init; } = true;

    public bool ShowMemory { get; init; } = true;

    public bool ShowDisk { get; init; } = true;

    /// <summary>The storage key this is kept under, within the instance's own slice.</summary>
    public const string StorageKey = "metrics";

    /// <summary>All three — what a freshly placed monitor shows before anyone opens its settings.</summary>
    public static SystemMonitorMetrics Default { get; } = new();

    /// <summary>
    /// Guards against the one state the form can produce that the widget cannot render: everything off, which
    /// would leave an empty pane looking broken. Falls back to showing all three.
    /// </summary>
    public SystemMonitorMetrics OrDefaultWhenEmpty() =>
        ShowCpu || ShowMemory || ShowDisk ? this : Default;
}
