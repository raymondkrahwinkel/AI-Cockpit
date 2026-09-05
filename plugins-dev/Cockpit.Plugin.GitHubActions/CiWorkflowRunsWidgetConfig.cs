namespace Cockpit.Plugin.GitHubActions;

// One dock-panel instance's own settings (AC-1065), mirroring GitHubPullRequestsWidgetConfig: a dashboard pane
// is sized by hand — a tall one has room for twenty, a short one for three — so how many runs it shows is per
// instance, not a plugin-wide setting.
internal sealed record CiWorkflowRunsWidgetConfig
{
    // Fewest and most a pane may show — the same 1–20 range the pull-requests pane uses.
    public const int MinItems = 1;
    public const int MaxItemsAllowed = 20;

    // How many recent workflow runs this pane lists.
    public int MaxItems { get; init; } = 10;

    // The storage key this is kept under, within the instance's own slice.
    public const string StorageKey = "widget";

    // What a freshly placed panel shows before anyone opens its settings.
    public static CiWorkflowRunsWidgetConfig Default { get; } = new();

    // Clamps a possibly out-of-range or zero count (an older or hand-edited config) back into 1–20.
    public CiWorkflowRunsWidgetConfig Sanitized() =>
        MaxItems is >= MinItems and <= MaxItemsAllowed
            ? this
            : this with { MaxItems = Math.Clamp(MaxItems, MinItems, MaxItemsAllowed) };
}
