namespace Cockpit.Core.Depot;

// One file `read_many` returned content for.
public sealed record DepotReadFile(string Path, string Content, string? Checksum);

public enum DepotReadManyOutcome
{
    Success,
    AuthorizationRequired,
    Failed,
}

// A batch read of the memory tree (AC-280 criterion 2), across as many `read_many` rounds as `paths` needs.
// `Missing` is Depot's own "this path doesn't exist"; `Unreadable` is this client's addition for a path whose
// round failed outright — conflating the two could read "Depot was unreachable" as "the file was deleted".
public sealed record DepotReadManyResult(
    DepotReadManyOutcome Outcome,
    IReadOnlyList<DepotReadFile> Files,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Unreadable,
    string? Error)
{
    public static DepotReadManyResult Success(
        IReadOnlyList<DepotReadFile> files, IReadOnlyList<string> missing, IReadOnlyList<string> unreadable) =>
        new(DepotReadManyOutcome.Success, files, missing, unreadable, null);

    public static DepotReadManyResult AuthorizationRequired { get; } =
        new(DepotReadManyOutcome.AuthorizationRequired, [], [], [], null);

    public static DepotReadManyResult Failed(string error) =>
        new(DepotReadManyOutcome.Failed, [], [], [], error);
}
