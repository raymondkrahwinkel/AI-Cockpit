using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mentions;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;

namespace Cockpit.Infrastructure.Mentions;

internal sealed class ProjectMentionSource(IProjectCatalog catalog) : IMentionSource, ISingletonService
{
    public string Key => "projects";

    public int Order => 10;

    public Task<IReadOnlyList<MentionGroup>> GetAsync(MentionContext context, CancellationToken cancellationToken)
    {
        var snapshot = catalog.Current;
        var groups = new List<MentionGroup>();

        var local = snapshot.Settings.Projects.Select(project => _Local(project, snapshot)).ToList();
        if (local.Count > 0)
        {
            groups.Add(new MentionGroup("Projects on this PC", local));
        }

        // The catalog already left out offers that are bound here, so none is filtered again.
        foreach (var group in snapshot.SharedProjectGroups.Where(group => group.Error is null && group.Projects.Count > 0))
        {
            groups.Add(new MentionGroup(group.SourceName, group.Projects.Select(_Shared).ToList(), $"via {group.SourceName}"));
        }

        return Task.FromResult<IReadOnlyList<MentionGroup>>(groups);
    }

    private static MentionItem _Local(Project project, ProjectCatalogSnapshot snapshot)
    {
        var path = project.SourceDirectory;
        var line = snapshot.BoundProjectLocations.TryGetValue(project.Id, out var location)
            ? $"{location.SourceName} {(location.Organization is null ? "" : location.Organization.Slug + "/")}{project.Name}"
            : "";
        if (!string.IsNullOrEmpty(path))
        {
            line = line.Length > 0 ? $"{line} · {path}" : path;
        }

        return new MentionItem(
            $"project:{project.Id}", project.Name, line, new MentionTile(_Letter(project.Name), project.LogoPath),
            MentionKind.Project, project.Name, path, project.LastOpenedAt);
    }

    private static MentionItem _Shared(SharedProjectOffer offer) =>
        new($"shared:{offer.Id}", offer.Name, offer.Description ?? "", new MentionTile(_Letter(offer.Name)),
            MentionKind.Project, offer.Name, offer.Id);

    private static string _Letter(string name) => name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
}
