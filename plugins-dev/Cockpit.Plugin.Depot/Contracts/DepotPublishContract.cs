namespace Cockpit.Plugin.Depot.Contracts;

// AC-1574: what the publications section of the file browser asks the backend part over the channel. The source is a
// path on the machine the backend runs on; a run publishes from there, so the same code serves a server.
internal static class DepotPublishChannel
{
    public const string List = "publish-list";
    public const string Add = "publish-add";
    public const string Remove = "publish-remove";
    public const string Plan = "publish-plan";
    public const string Run = "publish-run";
}

// One request shape for every action. `Confirmed` is the operator's yes to the plan; `OverwriteConflicts` the second,
// separate yes for files that changed in Depot since the last publication.
internal sealed record DepotPublishRequest(
    string ConnectionId, string? Project = null, string? Id = null, string? Source = null, string? TargetFolder = null,
    bool Confirmed = false, bool OverwriteConflicts = false);

// A link between a local folder (or file) and a folder in a Depot project. Settings, so it travels with a backup.
internal sealed record DepotPublication(string Id, string ConnectionId, string Project, string Source, string TargetFolder);

internal static class DepotPublishAction
{
    public const string New = "new";
    public const string Changed = "changed";
    public const string Unchanged = "unchanged";
    public const string Adopt = "adopt";
    public const string Conflict = "conflict";
    public const string Delete = "delete";
    public const string DeleteConflict = "delete-conflict";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

// `Path` is relative to the source, with `/` as separator; `Detail` is the reason for a skipped, conflicting or failed item.
internal sealed record DepotPublishItem(string Path, string Kind, string Action, string? Detail = null);

internal sealed record DepotPublishPlan(IReadOnlyList<DepotPublishItem> Items);

// `Problems` holds what a run did not do: conflicts it was not allowed to overwrite, skipped files and failures.
internal sealed record DepotPublishReport(
    DateTimeOffset At, int Written, int Adopted, int Deleted, int Unchanged, IReadOnlyList<DepotPublishItem> Problems);

internal sealed record DepotPublicationRow(DepotPublication Publication, DepotPublishReport? Last);
