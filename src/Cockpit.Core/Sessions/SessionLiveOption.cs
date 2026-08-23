namespace Cockpit.Core.Sessions;

// One control a running session can switch mid-conversation (#45 D4). The provider owns the vocabulary —
// names, labels, values — so the host renders it without knowing what it means; the core-side form the
// adapter maps a plugin's `PluginSessionLaunchOption` onto (kept separate so Core needs no plugin reference).
public sealed record SessionLiveOption(
    string Key,
    string Label,
    IReadOnlyList<string> Choices,
    string? CurrentValue)
{
    // A friendly label per `Choices` value (Claude's "Ask permissions" for `default`, etc.). A value with
    // no entry shows itself; the value sent back to the driver is always the raw `Choices` entry, never the label.
    public IReadOnlyDictionary<string, string>? ChoiceLabels { get; init; }
}
