namespace Cockpit.Core.Projects;

// AC-1435: the Projects page's read model. OwnershipClaims maps a claimed project's id to the source named on its
// badge, or to null when the claim names none; RemoteChangedProjectIds are the projects Depot reported changed.
public sealed record ProjectCatalogSnapshot(
    ProjectSettings Settings,
    IReadOnlyList<SharedProjectSourceInfo> Sources,
    IReadOnlyList<SharedProjectGroup> SharedProjectGroups,
    IReadOnlyDictionary<string, string?> OwnershipClaims,
    IReadOnlySet<string> RemoteChangedProjectIds)
{
    public static ProjectCatalogSnapshot Empty { get; } = new(
        ProjectSettings.Empty, [], [], new Dictionary<string, string?>(), new HashSet<string>());
}

public sealed record SharedProjectSourceInfo(string Key, string Name, bool CanPublish);

// A source that could not be read carries its own error rather than failing the other groups (AC-245).
// AC-1758: always one group per connection, empty or stale too; SourceKey tells two connections apart, and Notice is
// the stale-list line ("last known list · <time>") that rides a successful read, kept apart from a real Error.
public sealed record SharedProjectGroup(
    string SourceName, IReadOnlyList<SharedProjectOffer> Projects, string? Error, byte[]? SourceLogo = null,
    string SourceKey = "", string? Notice = null);

public sealed record SharedProjectOffer(
    string Id, string Name, string? Description = null, string? Role = null, SharedProjectOrganizationInfo? Organization = null);

// Core carries no Plugins.Abstractions reference, so the organization is copied by value.
public sealed record SharedProjectOrganizationInfo(string Slug, string? Name, string? Role);
