namespace Cockpit.Plugins.Abstractions.Health;

// AC-1466: one line of a health section. `At` is when the line last held, for a line that has a time.
public sealed record PluginHealthRow(string Label, PluginHealthStatus Status, DateTimeOffset? At = null);
