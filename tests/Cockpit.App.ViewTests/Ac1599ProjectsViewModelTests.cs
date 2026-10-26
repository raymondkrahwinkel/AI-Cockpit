using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Projects;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.App.ViewTests;

public sealed class Ac1599ProjectsViewModelTests
{
    [Fact]
    public async Task Sections_AreOrderedByMachineThenOrganizationNameThenHost_AndKeepBoundProjectLocation()
    {
        var machine = Project.Create("Machine only");
        var bound = Project.Create("Alpha") with { MemoryRef = "depot-a:alpha" };
        var catalog = new _Catalog(_Snapshot(
            [machine, bound],
            [
                _Group("depot-b", "Depot — beta.example", "Acme", "depot-b:beta", "Beta"),
                _Group("depot-a", "Depot — alpha.example", "Acme", "depot-a:alpha-team", "Alpha team"),
            ],
            new Dictionary<string, SharedProjectLocation> { [bound.Id] = _Location("depot-a", "Depot — alpha.example", "Acme") }));
        var viewModel = new ProjectsViewModel(catalog, Substitute.For<IProjectEditor>(), dialogs: null);

        await viewModel.LoadAsync();

        Assert.Collection(viewModel.ProjectSections,
            section => Assert.Equal("This machine", section.Name),
            section =>
            {
                Assert.Equal(("Acme", "alpha.example"), (section.Name, section.Host));
                Assert.Equal("Alpha", section.Items[0].Name);
            },
            section => Assert.Equal(("Acme", "beta.example"), (section.Name, section.Host)));
    }

    [Fact]
    public async Task Filters_KeepSectionTotalsAndShownCountsAccurate()
    {
        var catalog = new _Catalog(_Snapshot(
            [Project.Create("Cockpit") with { Category = "Prive" }],
            [_Group("depot-a", "Depot — alpha.example", "Acme", "depot-a:payroll", "Payroll", "Monthly payroll export")]));
        var viewModel = new ProjectsViewModel(catalog, Substitute.For<IProjectEditor>(), dialogs: null);

        await viewModel.LoadAsync();
        viewModel.SearchText = "payroll";

        var section = Assert.Single(viewModel.ProjectSections);
        Assert.Equal("Acme", section.Name);
        Assert.Equal(0, section.HereCount);
        Assert.Equal(1, section.ToAddCount);
        Assert.Equal(1, section.TotalCount);
        Assert.Equal(1, section.ShownCount);
        Assert.Equal("Payroll", Assert.Single(section.Items).Name);
    }

    [Fact]
    public async Task HideAndUnhide_ReloadTheSectionAndKeepHiddenCount()
    {
        var settings = ProjectSettings.Empty;
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(settings));
        store.SaveAsync(Arg.Any<ProjectSettings>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                settings = call.Arg<ProjectSettings>();
                return Task.CompletedTask;
            });
        var source = Substitute.For<ISharedProjectSource>();
        source.Key.Returns("depot-a");
        source.SourceName.Returns("Depot — alpha.example");
        source.ListAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(
            SharedProjectListResult.Success([
                new SharedProject("depot-a:payroll", "Payroll")
                {
                    Organization = new SharedProjectOrganization("acme", "Acme", "Editor"),
                },
            ])));
        var registry = new SharedProjectSourceRegistry();
        registry.Register(source);
        var catalog = new ProjectCatalog(store, new ProjectOwnershipRegistry(), registry);
        var viewModel = new ProjectsViewModel(catalog, Substitute.For<IProjectEditor>(), dialogs: null);

        await viewModel.LoadAsync();
        await viewModel.LoadSharedProjectsAsync();
        await viewModel.HideSharedProjectAsync("depot-a:payroll");

        Assert.Contains("depot-a:payroll", settings.HiddenSharedProjectIds);
        await viewModel.LoadAsync();
        await viewModel.LoadSharedProjectsAsync();
        var hidden = Assert.Single(viewModel.ProjectSections);
        Assert.Equal(1, hidden.HiddenCount);
        Assert.Empty(hidden.Items);

        await viewModel.UnhideSharedProjectsAsync(("depot-a", "acme"));

        Assert.Empty(settings.HiddenSharedProjectIds);
        await viewModel.LoadAsync();
        await viewModel.LoadSharedProjectsAsync();
        var restored = Assert.Single(viewModel.ProjectSections);
        Assert.Equal(0, restored.HiddenCount);
        Assert.Equal("Payroll", Assert.Single(restored.Items).Name);
    }

    private static ProjectCatalogSnapshot _Snapshot(
        IReadOnlyList<Project> projects,
        IReadOnlyList<SharedProjectGroup> groups,
        IReadOnlyDictionary<string, SharedProjectLocation>? boundLocations = null) =>
        new(new ProjectSettings { Projects = projects }, [], groups, new Dictionary<string, string?>(), new HashSet<string>())
        {
            BoundProjectLocations = boundLocations ?? new Dictionary<string, SharedProjectLocation>(),
        };

    private static SharedProjectGroup _Group(string sourceKey, string sourceName, string organization, string id, string name, string? description = null) =>
        new(sourceName, [new SharedProjectOffer(id, name, description, Organization: new SharedProjectOrganizationInfo(organization.ToLowerInvariant(), organization, "Editor"))], null, SourceKey: sourceKey);

    private static SharedProjectLocation _Location(string sourceKey, string sourceName, string organization) =>
        new(sourceKey, sourceName, new SharedProjectOrganizationInfo(organization.ToLowerInvariant(), organization, "Editor"));

    private sealed class _Catalog(ProjectCatalogSnapshot current) : IProjectCatalog
    {
        private readonly Dictionary<string, (SharedProjectGroup Group, SharedProjectOffer Offer)> _offers =
            current.SharedProjectGroups.SelectMany(group => group.Projects.Select(offer => (offer.Id, (group, offer))))
                .ToDictionary(pair => pair.Id, pair => pair.Item2, StringComparer.Ordinal);

        public ProjectCatalogSnapshot Current { get; private set; } = current;

        public event Action? Changed;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RefreshSharedProjectsAsync(CancellationToken cancellationToken = default)
        {
            Changed?.Invoke();
            return Task.CompletedTask;
        }

        public Task SyncNowAsync(string projectId) => Task.CompletedTask;

        public Task HideSharedProjectAsync(string sharedProjectId)
        {
            var (group, offer) = _offers[sharedProjectId];
            var location = new SharedProjectLocation(group.SourceKey, group.SourceName, offer.Organization);
            Current = Current with
            {
                SharedProjectGroups = Current.SharedProjectGroups.Select(candidate => candidate == group
                    ? candidate with { Projects = candidate.Projects.Where(item => item.Id != sharedProjectId).ToList() }
                    : candidate).ToList(),
                HiddenSharedProjectLocations = new Dictionary<string, SharedProjectLocation>(Current.HiddenSharedProjectLocations)
                {
                    [sharedProjectId] = location,
                },
            };
            return Task.CompletedTask;
        }

        public Task UnhideSharedProjectsAsync(string sourceKey, string organizationSlug)
        {
            var ids = Current.HiddenSharedProjectLocations
                .Where(pair => pair.Value.SourceKey == sourceKey && pair.Value.Organization?.Slug == organizationSlug)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.Ordinal);
            Current = Current with
            {
                SharedProjectGroups = Current.SharedProjectGroups.Select(group => group with
                {
                    Projects = [.. group.Projects.Concat(_offers.Where(pair => ids.Contains(pair.Key) && pair.Value.Group.SourceKey == group.SourceKey).Select(pair => pair.Value.Offer))],
                }).ToList(),
                HiddenSharedProjectLocations = Current.HiddenSharedProjectLocations.Where(pair => !ids.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            };
            return Task.CompletedTask;
        }
    }
}
