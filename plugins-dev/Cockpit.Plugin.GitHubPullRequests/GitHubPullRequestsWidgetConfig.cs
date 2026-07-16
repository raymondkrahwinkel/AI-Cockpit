namespace Cockpit.Plugin.GitHubPullRequests;

/// <summary>
/// One dashboard widget instance's own settings (#AC-18). The widget mirrors the side-menu section, but a
/// dashboard pane is sized by hand — a tall one has room for twenty, a short one for three — so how many pull
/// requests it shows is per instance, not the plugin-wide count the section uses.
/// </summary>
/// <remarks>
/// A record round-tripped through the instance's <c>IPluginStorage</c> as one JSON value, so a half-written
/// config cannot leave the widget with a nonsense count. Kept small on purpose: connection, repositories and
/// the prompt template stay shared plugin settings — this is only what a single pane decides for itself.
/// </remarks>
internal sealed record GitHubPullRequestsWidgetConfig
{
    /// <summary>Fewest and most a pane may show — the same 1–20 range the section's count is clamped to.</summary>
    public const int MinItems = 1;
    public const int MaxItemsAllowed = 20;

    /// <summary>How many pull requests this pane lists. Defaults to ten — a dashboard pane is roomier than the side strip's five.</summary>
    public int MaxItems { get; init; } = 10;

    /// <summary>The storage key this is kept under, within the instance's own slice.</summary>
    public const string StorageKey = "widget";

    /// <summary>What a freshly placed widget shows before anyone opens its settings.</summary>
    public static GitHubPullRequestsWidgetConfig Default { get; } = new();

    /// <summary>Clamps a possibly out-of-range or zero count (an older or hand-edited config) back into 1–20.</summary>
    public GitHubPullRequestsWidgetConfig Sanitized() =>
        MaxItems is >= MinItems and <= MaxItemsAllowed
            ? this
            : this with { MaxItems = Math.Clamp(MaxItems, MinItems, MaxItemsAllowed) };
}
