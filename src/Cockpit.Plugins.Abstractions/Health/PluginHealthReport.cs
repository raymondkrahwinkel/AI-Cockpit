namespace Cockpit.Plugins.Abstractions.Health;

// AC-1466: what a health section reports. Healthy alone decides /healthz; the rows say why.
public sealed record PluginHealthReport(bool Healthy, IReadOnlyList<PluginHealthRow> Rows);
