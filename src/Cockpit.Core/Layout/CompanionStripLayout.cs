namespace Cockpit.Core.Layout;

public sealed record CompanionStripLayout
{
    public IReadOnlyList<string> Order { get; init; } = [];
    public IReadOnlyList<string> HiddenIds { get; init; } = [];
    public IReadOnlyList<string> SeenIds { get; init; } = [];
    public IReadOnlyDictionary<string, string> Titles { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> PluginIds { get; init; } = new Dictionary<string, string>();
}
