using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Projects;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.App.ViewTests;

[Collection("avalonia")]
public sealed class Ac1599ProjectsViewModelTests
{
    [Fact]
    public Task Sections_AreOrderedByMachineThenOrganizationNameThenHost_AndKeepBoundProjectLocation() => HeadlessAvalonia.RunAsync(async () =>
    {
        var prototype = _PrototypeSnapshot();
        var catalog = new _Catalog(prototype with
        {
            SharedProjectGroups = [
                _Group("depot-b", "Depot — Beta", "beta.example", "Synvolution", "depot-b:beta", "Beta"),
                .. prototype.SharedProjectGroups,
            ],
        });
        var viewModel = new ProjectsViewModel(catalog, Substitute.For<IProjectEditor>(), dialogs: null);
        var sessions = new System.Collections.ObjectModel.ObservableCollection<string> { "cockpit", "cockpit", "cockpit", "syn-flow", "depot" };
        viewModel.SetSessionCounter(id => sessions.Count(projectId => projectId == id), sessions);

        await viewModel.LoadAsync();

        Assert.Collection(viewModel.ProjectSections,
            section => Assert.Equal("This machine", section.Name),
            section =>
            {
                Assert.Equal("Krahwinkel IT", section.Name);
                Assert.Equal("kit-infra", section.Items[0].Id);
            },
            section =>
            {
                Assert.Equal("Persoonlijk", section.Name);
                Assert.Equal(3, section.Items.Single(item => item.Id == "cockpit").SessionsRunning);
            },
            section =>
            {
                Assert.Equal(("Synvolution", "alpha.example"), (section.Name, section.Host));
                Assert.Equal(
                    ["syn-cicd", "syn-flow", "payroll", "mailcatch", "depot-a:t-gw", "depot-a:t-ds", "depot-a:t-portal", "depot-a:t-ops", "depot-a:t-rooster", "depot-a:t-docs", "depot-a:t-hr", "depot-a:t-id"],
                    section.Items.Select(item => item.Id));
            },
            section => Assert.Equal(("Synvolution", "beta.example"), (section.Name, section.Host)));

        sessions.Remove("cockpit");
        Assert.Equal(2, viewModel.ProjectSections.SelectMany(section => section.Items).Single(item => item.Id == "cockpit").SessionsRunning);
    });

    [Fact]
    public Task Filters_KeepSectionTotalsAndShownCountsAccurate() => HeadlessAvalonia.RunAsync(async () =>
    {
        var prototype = _PrototypeSnapshot();
        var catalog = new _Catalog(prototype with
        {
            SharedProjectGroups = prototype.SharedProjectGroups.Select(group => group with { Notice = "Last known list" }).ToList(),
        });
        var viewModel = new ProjectsViewModel(catalog, Substitute.For<IProjectEditor>(), dialogs: null);

        await viewModel.LoadAsync();
        Assert.Equal(28, viewModel.ProjectSections.Sum(section => section.TotalCount));
        Assert.Equal(4, viewModel.ProjectSections.Count);
        Assert.All(viewModel.ProjectSections.Where(section => !section.IsThisMachine), section => Assert.Equal("Last known list", section.Notice));
        Assert.Equal(["All", "Prive", "Synvolution", "Uncategorized"], viewModel.Categories);
        viewModel.SearchText = "payroll";

        var section = Assert.Single(viewModel.ProjectSections);
        Assert.Equal("Synvolution", section.Name);
        Assert.Equal(4, section.HereCount);
        Assert.Equal(8, section.ToAddCount);
        Assert.Equal(12, section.TotalCount);
        Assert.Equal(2, section.ShownCount);
        Assert.Equal(["Synvolution Flow", "Payroll Processor"], section.Items.Select(item => item.Name));

        viewModel.SearchText = "market calculator";
        Assert.Equal("evewb", Assert.Single(Assert.Single(viewModel.ProjectSections).Items).Id);
        viewModel.SearchText = "Persoonlijk";
        Assert.Equal(5, Assert.Single(viewModel.ProjectSections).ShownCount);
        viewModel.SearchText = string.Empty;
        viewModel.SelectedCategory = "Prive";
        Assert.Equal(9, viewModel.ProjectSections.Sum(candidate => candidate.ShownCount));
        viewModel.SelectedCategory = "Uncategorized";
        Assert.Equal(13, viewModel.ProjectSections.Sum(candidate => candidate.ShownCount));
        viewModel.SearchText = "no matching project";
        Assert.Empty(viewModel.ProjectSections);
    });

    [Fact]
    public Task HideAndUnhide_ReloadTheSectionAndKeepHiddenCount() => HeadlessAvalonia.RunAsync(async () =>
    {
        var settings = new ProjectSettings
        {
            Projects = [new Project("local-payroll", "My payroll") { MemoryRef = "depot-a:bound" }],
        };
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
                new SharedProject("depot-a:bound", "Bound")
                {
                    Organization = new SharedProjectOrganization("acme", "Acme", "Editor"),
                },
            ]) with
            {
                ProjectReadStates = [new SharedProjectReadStatus("depot-a:nodef", "No definition", "Editor", SharedProjectReadState.Absent)],
            }));
        var registry = new SharedProjectSourceRegistry();
        registry.Register(source);
        var catalog = new ProjectCatalog(store, new ProjectOwnershipRegistry(), registry);
        var viewModel = new ProjectsViewModel(catalog, Substitute.For<IProjectEditor>(), dialogs: null);

        await viewModel.LoadAsync();
        await viewModel.LoadSharedProjectsAsync();
        Assert.Equal("acme", catalog.Current.BoundProjectLocations["local-payroll"].Organization?.Slug);
        await viewModel.HideSharedProjectAsync("depot-a:payroll");

        Assert.Contains("depot-a:payroll", settings.HiddenSharedProjectIds);
        await viewModel.LoadAsync();
        await viewModel.LoadSharedProjectsAsync();
        var hidden = Assert.Single(viewModel.ProjectSections, section => section.OrganizationSlug == "acme");
        Assert.Equal(1, hidden.HiddenCount);
        Assert.Equal("local-payroll", Assert.Single(hidden.Items).Id);

        viewModel.SearchText = "no matching project";
        Assert.Empty(viewModel.ProjectSections);
        viewModel.SearchText = string.Empty;

        await viewModel.UnhideSharedProjectsAsync(("depot-a", "acme"));

        Assert.Empty(settings.HiddenSharedProjectIds);
        await viewModel.LoadAsync();
        await viewModel.LoadSharedProjectsAsync();
        var restored = Assert.Single(viewModel.ProjectSections, section => section.OrganizationSlug == "acme");
        Assert.Equal(0, restored.HiddenCount);
        Assert.Equal(["My payroll", "Payroll"], restored.Items.Select(item => item.Name));

        await viewModel.HideSharedProjectAsync("depot-a:nodef");
        Assert.Equal(1, Assert.Single(viewModel.ProjectSections, section => section.OrganizationSlug == string.Empty).HiddenCount);
        await viewModel.UnhideSharedProjectsAsync(("depot-a", string.Empty));
        Assert.Empty(settings.HiddenSharedProjectIds);
        Assert.Equal("No definition", Assert.Single(Assert.Single(viewModel.ProjectSections, section => section.OrganizationSlug == string.Empty).Items).Name);
    });

    // Fixed input from Designs/AC-1595/prototype/voorstel-b2.html, window.P (28 projects).
    private static ProjectCatalogSnapshot _PrototypeSnapshot()
    {
        var owned = new (string Id, string Name, string? Org, string? Category, int? DaysAgo, string Description)[]
        {
            ("syn-cicd", "Synvolution-CICD", "Synvolution", "Synvolution", 0, "Pipelines, runners and release automation for every Synvolution service."),
            ("syn-flow", "Synvolution Flow", "Synvolution", "Synvolution", 1, "Workflow engine and designer. Owns the flow definitions, the execution runtime, the connectors to payroll and HR systems, and the customer-facing designer UI. Migrating from the v3 runtime to v4 this quarter; see the epic for the cut-over plan."),
            ("payroll", "Payroll Processor", "Synvolution", "Synvolution", 3, "Monthly payroll runs, mutations import and the loonstrook export."),
            ("mailcatch", "Synvolution-MailCatch", "Synvolution", "Synvolution", 30, "Catches outgoing mail in test environments so nothing reaches a real inbox."),
            ("kit-site", "krahwinkel-it.nl", "Krahwinkel IT", null, 14, "Company website and the contact form."),
            ("kit-infra", "Infra & Monitoring", "Krahwinkel IT", null, 5, "Servers, backups, Beszel and the alerting rules."),
            ("evet", "EVE Together", "Persoonlijk", "Prive", 1, "Fleet tools for playing EVE Online with friends: shared fittings, ops calendar and a doctrine checker."),
            ("cockpit", "Cockpit", "Persoonlijk", "Prive", 0, "The desktop cockpit for running AI sessions side by side — profiles, projects, plugins and the server."),
            ("depot", "Depot", "Persoonlijk", "Prive", 1, "Storage and truth layer: memory, artifacts, orgs, seats and the cockpit manifest."),
            ("notes", "Notes-Taker", "Persoonlijk", "Prive", 2, "Meeting notes that land in the Depot inbox, with action items per project."),
            ("homelab", "Homelab", "Persoonlijk", "Prive", 21, "Home Assistant, the NAS and the network at home."),
            ("sj", "StudentJourney-App", null, "Synvolution", 7, ""),
            ("mkt", "Marketing automation", null, "Synvolution", 14, "Campaign triggers and mailing lists."),
            ("evewb", "EVE Workbench", null, "Prive", 4, "Industry and market calculator for manufacturing chains."),
            ("aihub", "AI-Hub", null, "Prive", 21, "Experiments with local models."),
            ("brand", "Brand Studio", null, null, null, ""),
            ("garmin", "Garmin-Sync", null, "Prive", 60, "Pulls sleep and activity data into the notes."),
            ("scratch", "Scratchpad", null, null, 1, "Throwaway experiments."),
            ("ewb-old", "EVE Workbench (legacy WinForms version, kept for the 2019 market data import)", null, "Prive", 365, "Read-only. Kept only because the old market-data importer has not been ported yet."),
        };
        var lastOpened = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var projects = owned.Select(row => new Project(row.Id, row.Name)
        {
            MemoryRef = row.Org is null ? null : "depot-a:" + row.Id,
            Description = row.Description,
            Category = row.Category,
            LastOpenedAt = row.DaysAgo is { } days ? lastOpened.AddDays(-days) : null,
        }).ToList();
        var team = new (string Id, string Name, string Org, string? Description)[]
        {
            ("t-hr", "Synvolution HR-Connector", "Synvolution", "Koppeling met AFAS en Nmbrs voor het inlezen van medewerkers, contracten en verlof."),
            ("t-docs", "Synvolution Docs", "Synvolution", "Developer documentation site."),
            ("t-portal", "Klantportaal", "Synvolution", null),
            ("t-ops", "ops-runbooks", "Synvolution", null),
            ("t-id", "Synvolution Identity", "Synvolution", "Single sign-on, tenants and the user directory."),
            ("t-rooster", "Rooster-planner", "Synvolution", "Shift planning for care teams, with availability and leave."),
            ("t-gw", "API Gateway", "Synvolution", "Routing, rate limits and API keys in front of every public service."),
            ("t-ds", "Design-System", "Synvolution", "Shared components and tokens for the Synvolution apps."),
            ("kit-cust", "Klant – De Vries Installatietechniek", "Krahwinkel IT", "Maintenance contract: Microsoft 365 tenant, laptops and the NAS."),
        };
        var groups = team.GroupBy(row => row.Org).Select(group => new SharedProjectGroup(
            "Depot — Alpha",
            group.Select(row => new SharedProjectOffer("depot-a:" + row.Id, row.Name, row.Description,
                Organization: new SharedProjectOrganizationInfo(group.Key.ToLowerInvariant(), group.Key, "Editor"))).ToList(),
            null, SourceKey: "depot-a", SourceHost: "alpha.example")).ToList();
        return _Snapshot(projects, groups, owned.Where(row => row.Org is not null).ToDictionary(
            row => row.Id, row => _Location("depot-a", "Depot — Alpha", "alpha.example", row.Org!))) with
        {
            Settings = new ProjectSettings { Projects = projects, CategoryOrder = ["Prive", "Synvolution"] },
        };
    }

    private static ProjectCatalogSnapshot _Snapshot(
        IReadOnlyList<Project> projects,
        IReadOnlyList<SharedProjectGroup> groups,
        IReadOnlyDictionary<string, SharedProjectLocation>? boundLocations = null) =>
        new(new ProjectSettings { Projects = projects }, [], groups, new Dictionary<string, string?>(), new HashSet<string>())
        {
            BoundProjectLocations = boundLocations ?? new Dictionary<string, SharedProjectLocation>(),
        };

    // AC-1600: the host travels as its own field, apart from the display name, which names no host here on purpose.
    private static SharedProjectGroup _Group(string sourceKey, string sourceName, string host, string organization, string id, string name, string? description = null) =>
        new(sourceName, [new SharedProjectOffer(id, name, description, Organization: new SharedProjectOrganizationInfo(organization.ToLowerInvariant(), organization, "Editor"))], null, SourceKey: sourceKey, SourceHost: host);

    private static SharedProjectLocation _Location(string sourceKey, string sourceName, string host, string organization) =>
        new(sourceKey, sourceName, new SharedProjectOrganizationInfo(organization.ToLowerInvariant(), organization, "Editor"), host);

    private sealed class _Catalog(ProjectCatalogSnapshot current) : IProjectCatalog
    {
        public ProjectCatalogSnapshot Current { get; } = current;

        public event Action? Changed;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RefreshSharedProjectsAsync(CancellationToken cancellationToken = default)
        {
            Changed?.Invoke();
            return Task.CompletedTask;
        }

        public Task SyncNowAsync(string projectId) => Task.CompletedTask;

        public Task HideSharedProjectAsync(string sharedProjectId) => throw new NotSupportedException();

        public Task UnhideSharedProjectsAsync(string sourceKey, string organizationSlug) => throw new NotSupportedException();
    }
}
