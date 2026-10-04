namespace Cockpit.Plugins.Abstractions.Health;

// AC-1466: one line of a health section. `At` is when the line last held, for a line that has a time. The label is a
// fixed English label naming the thing; never a path, host, user or secret. The host cuts it to 120 characters.
public sealed record PluginHealthRow(string Label, PluginHealthStatus Status, DateTimeOffset? At = null)
{
    // AC-1470: the cockpit project this line belongs to, if any. A line with a project reaches only a connect key
    // whose scope holds that project; a line without one reaches every key that may read the health.
    public string? ProjectId { get; init; }

    // AC-1470: the action this line offers, run through the section's IPluginHealthActions. A slug-like id of at most
    // 64 characters, unique to this line: an id that a line outside the caller's scope also offers is not run.
    public string? ActionId { get; init; }

    // AC-1477: when this line runs, as readable text ("daily 09:00", "every 15m"), and the time zone that text is read
    // in. Set only when the plugin could read the schedule itself. Same rules as the label; the host cuts both to 120.
    public string? Schedule { get; init; }

    public string? TimeZone { get; init; }
}
