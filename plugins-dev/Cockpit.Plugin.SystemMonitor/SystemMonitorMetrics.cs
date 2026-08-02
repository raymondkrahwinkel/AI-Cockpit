namespace Cockpit.Plugin.SystemMonitor;

// Which of the three readings a System Monitor instance shows — the widget's whole configuration, and the
// reason it has a settings form at all. Stored per instance, so two monitors on one dashboard can show
// different things.
// A record rather than three loose storage keys: it round-trips through `IPluginStorage` as one JSON
// value, so a partially-written config cannot leave the widget showing nothing.
internal sealed record SystemMonitorMetrics
{
    public bool ShowCpu { get; init; } = true;

    public bool ShowMemory { get; init; } = true;

    public bool ShowDisk { get; init; } = true;

    // The storage key this is kept under, within the instance's own slice.
    public const string StorageKey = "metrics";

    // All three — what a freshly placed monitor shows before anyone opens its settings.
    public static SystemMonitorMetrics Default { get; } = new();

    // Guards against the one state the form can produce that the widget cannot render: everything off, which
    // would leave an empty pane looking broken. Falls back to showing all three.
    public SystemMonitorMetrics OrDefaultWhenEmpty() =>
        ShowCpu || ShowMemory || ShowDisk ? this : Default;
}
