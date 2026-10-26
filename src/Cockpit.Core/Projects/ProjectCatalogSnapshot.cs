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
// AC-1758: one group per connection. Notice is the stale "last known list" line, kept apart from Error; AllAdded
// means every listed project is already bound or hidden here.
public sealed record SharedProjectGroup(
    string SourceName, IReadOnlyList<SharedProjectOffer> Projects, string? Error, byte[]? SourceLogo = null,
    string SourceKey = "", string? Notice = null, bool AllAdded = false);

// AC-1758: Unavailable says why a listed project cannot be added yet (no Cockpit definition, say); null means it can.
public sealed record SharedProjectOffer(
    string Id, string Name, string? Description = null, string? Role = null, SharedProjectOrganizationInfo? Organization = null,
    string? Unavailable = null)
{
    public bool CanAdd => Unavailable is null;
}

// Core carries no Plugins.Abstractions reference, so the organization is copied by value.
public sealed record SharedProjectOrganizationInfo(string Slug, string? Name, string? Role);
