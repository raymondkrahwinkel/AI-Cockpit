namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// What came of asking an <see cref="ISharedProjectSource"/> for its publish targets (AC-620) — the same
/// whole-call-failure idiom <see cref="SharedProjectListResult"/> already keeps for <see cref="ISharedProjectSource.ListAsync"/>.
/// </summary>
public sealed record SharedProjectPublishTargetListResult(bool Succeeded, IReadOnlyList<SharedProjectPublishTarget> Targets, string? Error)
{
    public static SharedProjectPublishTargetListResult Success(IReadOnlyList<SharedProjectPublishTarget> targets) => new(true, targets, null);

    public static SharedProjectPublishTargetListResult Failed(string error) => new(false, [], error);
}
