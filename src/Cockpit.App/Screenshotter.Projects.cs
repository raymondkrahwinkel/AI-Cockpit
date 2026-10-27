using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Workspaces;

namespace Cockpit.App;

// AC-1600: the Projects-workspace scenes, in their own file so the B2 tickets after this one (AC-1601…1605) each add
// rows here without touching the main table. Every scene stages a catalog snapshot rather than the view model's
// collections, so the list is built by the same code the running cockpit uses.
internal static partial class Screenshotter
{
    private static IEnumerable<KeyValuePair<string, Func<int, int, Window>>> ProjectScenes =>
    [
        // B2 next to mockup b2-01 (1100) and b2-02 (1920): two Depot connections, three organisations and this machine.
        new("projects-workspace", (_, _) => _ProjectsWorkspace(_StageTwoConnections)),
        new("projects-workspace-compact", (_, _) => _ProjectsWorkspace(cockpit =>
        {
            _StageTwoConnections(cockpit);
            cockpit.Projects.Density = ProjectsDensity.Compact;
        })),
        new("projects-workspace-narrow", (_, _) => _ProjectsWorkspace(_StageTwoConnections)),
        new("projects-workspace-narrow-detail", (_, _) => _ProjectsWorkspaceNarrowDetail()),
        // AC-488: nothing staged, the only state the starting-point gallery is drawn in.
        new("projects-workspace-empty", (_, _) => _ProjectsWorkspace(stage: null)),
        // A team project the source listed but cannot offer yet sits beside one it can (AC-1758).
        new("projects-workspace-team-partial", (_, _) => _ProjectsWorkspace(_StagePartialTeamProjects)),
        // Raymond, 26-10: a server that fails with no known list gets one section on its connection, with the reason
        // and Try again; the other server's sections are unaffected.
        new("projects-workspace-unreachable", (_, _) => _ProjectsWorkspace(_StageUnreachableServer)),
        new("projects-workspace-loading", (_, _) => _ProjectsWorkspace(_StageLoading)),
        new("projects-workspace-stale", (_, _) => _ProjectsWorkspace(_StageStaleServer)),
        new("projects-workspace-long-text", (_, _) => _ProjectsWorkspace(_StageLongProjectText)),
        // AC-1601: the detail pane laid out, next to b2-23 (a local project with sessions and jobs), b2-12 (a team project to add) and
        // b2-20 (the Start split open).
        new("projects-workspace-detail", (_, _) => _ProjectsWorkspace(cockpit => _StageTwoConnectionsSelecting(cockpit, "Cockpit"))),
        new("projects-workspace-detail-team", (_, _) => _ProjectsWorkspace(cockpit => _StageTwoConnectionsSelecting(cockpit, "API Gateway"))),
        new("projects-workspace-split-open", (_, _) => _ProjectsWorkspace(_StageTwoConnections)),
        new("projects-workspace-more-open", (_, _) => _ProjectsWorkspace(_StageTwoConnections)),
        new("projects-workspace-hidden", (_, _) => _ProjectsWorkspace(_StageTwoConnectionsWithHidden)),
    ];

    private static Dictionary<string, Func<int, int, Window>> _WithProjectScenes(Dictionary<string, Func<int, int, Window>> scenes)
    {
        foreach (var (name, build) in ProjectScenes)
        {
            scenes.Add(name, build);
        }

        return scenes;
    }

    private static MainWindow _ProjectsWorkspace(Action<CockpitViewModel>? stage)
    {
        var cockpit = new CockpitViewModel();

        // A headless render has no `ISharedProjectSource` to list from, so the scene hands the view model what the
        // catalog would.
        cockpit.Projects.CardActions = new ProjectCardActions(
            cockpit.StartProjectSessionCommand,
            cockpit.NewSessionForProjectCommand,
            cockpit.StartProjectJobCommand,
            cockpit.EditProjectCommand,
            cockpit.OpenProjectFolderCommand,
            cockpit.ShareProjectCommand,
            cockpit.SyncProjectNowCommand,
            cockpit.SelectSessionCommand);
        // The design-time cockpit does not wire the running-session count the real one does.
        cockpit.Projects.SetSessionCounter(id => cockpit.Sessions.Count(session => session.ProjectId == id), cockpit.Sessions);
        cockpit.Projects.RunningSessionsOf = id => cockpit.Sessions.Where(session => session.ProjectId == id);
        // A scene builder returns a window synchronously; opening the workspace needs no I/O here, so blocking is safe.
        cockpit.Workspaces.OpenWorkspaceAsync(WorkspaceType.Projects.Id).GetAwaiter().GetResult();
        stage?.Invoke(cockpit);

        return new MainWindow { DataContext = cockpit, MinWidth = 0 };
    }

    private static MainWindow _ProjectsWorkspaceNarrowDetail()
    {
        var window = _ProjectsWorkspace(_StageTwoConnections);
        window.Opened += (_, _) =>
        {
            window.GetVisualDescendants().OfType<ProjectsWorkspace>().Single().ShowDetail();
            window.UpdateLayout();
        };
        return window;
    }

    private const string SynvolutionSource = "depot-synvolution";
    private const string KrahwinkelSource = "depot-krahwinkel";

    private static readonly SharedProjectOrganizationInfo _synvolution = new("synvolution", "Synvolution", "Member");
    private static readonly SharedProjectOrganizationInfo _krahwinkel = new("krahwinkel-it", "Krahwinkel IT", "Owner");
    private static readonly SharedProjectOrganizationInfo _personal = new("persoonlijk", "Persoonlijk", "Owner");

    private static readonly SharedProjectSourceInfo[] _sources =
    [
        new(SynvolutionSource, "Depot — Synvolution", CanPublish: true),
        new(KrahwinkelSource, "Depot — Krahwinkel IT", CanPublish: true),
    ];

    private static SharedProjectLocation _AtSynvolution() =>
        new(SynvolutionSource, "Depot — Synvolution", _synvolution, "depot.synvolution-software.nl");

    private static SharedProjectLocation _AtKrahwinkel(SharedProjectOrganizationInfo organization) =>
        new(KrahwinkelSource, "Depot — Krahwinkel IT", organization, "depot.krahwinkel-it.nl");

    private static Project _Project(string name, string? profile, TimeSpan? openedAgo, string? description = null, string? category = null) =>
        Project.Create(name) with
        {
            Description = description,
            DefaultProfileLabel = profile,
            Category = category,
            LastOpenedAt = openedAgo is { } ago ? DateTimeOffset.Now - ago : null,
            SourceDirectories = [new($@"C:\Users\raymo\RiderProjects\{name.Replace(" ", string.Empty, StringComparison.Ordinal)}")],
        };

    // The mockup's dataset (b2-01/b2-02), spread over two connections so each section heading names its own server.
    private static void _StageTwoConnections(CockpitViewModel cockpit) => _StageTwoConnections(cockpit, withHidden: false);

    private static void _StageTwoConnections(CockpitViewModel cockpit, bool withHidden)
    {
        var cicd = _Project("Synvolution-CICD", "Claude · Sonnet", TimeSpan.FromHours(2),
            "Pipelines, runners and release automation for every Synvolution service.", "Synvolution") with
        {
            Jobs = [new ProjectJob("Check the nightly pipelines.", "Reads only — changes nothing", new JobRecurrence(1, DayOfWeek.Monday))],
        };
        var flow = _Project("Synvolution Flow", "Claude · Opus", TimeSpan.FromDays(1),
            "Workflow engine and designer. Owns the flow definitions, the execution runtime, the connectors to payroll and HR systems, and the customer-facing designer UI. Migrating from the v3 runtime to v4 this quarter; see the epic for the cut-over plan.",
            "Synvolution");
        var payroll = _Project("Payroll Processor", "Claude · Sonnet", TimeSpan.FromDays(3),
            "Monthly payroll runs, mutations import and the loonstrook export.", "Synvolution");
        var mailCatch = _Project("Synvolution-MailCatch", "Codex", TimeSpan.FromDays(30),
            "Catches outgoing mail in test environments so nothing reaches a real inbox.", "Synvolution");
        var infra = _Project("Infra & Monitoring", "Claude · Opus", TimeSpan.FromDays(5),
            "Servers, backups, Beszel and the alerting rules.");
        var website = _Project("krahwinkel-it.nl", "Claude · Sonnet", TimeSpan.FromDays(14), "Company website and the contact form.");
        var cockpitProject = _Project("Cockpit", "Claude · Opus", TimeSpan.FromMinutes(0.5),
            "The desktop cockpit for running AI sessions side by side — profiles, projects, plugins and the server.", "Prive") with
        {
            Jobs =
            [
                new ProjectJob("Review the open pull requests", "Reads only — changes nothing", new JobRecurrence(1, DayOfWeek.Friday)),
                new ProjectJob("Triage new tickets", "Adds comments in YouTrack", new JobRecurrence(1, DayOfWeek.Monday)),
            ],
        };
        var eve = _Project("EVE Workbench", "Claude · Sonnet", TimeSpan.FromDays(4),
            "Industry and market calculator for manufacturing chains.", "Prive");
        var aiHub = _Project("AI-Hub", "Ollama · qwen3", TimeSpan.FromDays(21), "Experiments with local models.", "Prive");
        var brand = _Project("Brand Studio", profile: null, openedAgo: null);

        var bound = new Dictionary<string, SharedProjectLocation>(StringComparer.Ordinal)
        {
            [cicd.Id] = _AtSynvolution(),
            [flow.Id] = _AtSynvolution(),
            [payroll.Id] = _AtSynvolution(),
            [mailCatch.Id] = _AtSynvolution(),
            [infra.Id] = _AtKrahwinkel(_krahwinkel),
            [website.Id] = _AtKrahwinkel(_krahwinkel),
            [cockpitProject.Id] = _AtKrahwinkel(_personal),
        };

        SharedProjectOffer Offer(string id, string name, string? description, string role, SharedProjectOrganizationInfo organization) =>
            new(id, name, description, role, organization);

        var snapshot = ProjectCatalogSnapshot.Empty with
        {
            Sources = _sources,
            Settings = ProjectSettings.Empty with
            {
                Projects = [cicd, flow, payroll, mailCatch, infra, website, cockpitProject, eve, aiHub, brand],
                CategoryOrder = ["Synvolution", "Prive"],
            },
            SharedProjectGroups =
            [
                new("Depot — Synvolution",
                [
                    Offer($"{SynvolutionSource}:api-gateway", "API Gateway", "Routing, rate limits and API keys in front of every public service.", "Editor", _synvolution),
                    Offer($"{SynvolutionSource}:design-system", "Design-System", "Shared components and tokens for the Synvolution apps.", "Viewer", _synvolution),
                    Offer($"{SynvolutionSource}:klantportaal", "Klantportaal", null, "Editor", _synvolution),
                    Offer($"{SynvolutionSource}:rooster-planner", "Rooster-planner", "Shift planning for care teams, with availability and leave.", "Editor", _synvolution),
                    Offer($"{SynvolutionSource}:docs", "Synvolution Docs", "Developer documentation site.", "Viewer", _synvolution),
                ], Error: null, SourceKey: SynvolutionSource, SourceHost: "depot.synvolution-software.nl"),
                new("Depot — Krahwinkel IT",
                [
                    Offer($"{KrahwinkelSource}:de-vries", "Klant – De Vries Installatietechniek",
                        "Maintenance contract: Microsoft 365 tenant, laptops and the NAS.", "Owner", _krahwinkel),
                ], Error: null, SourceKey: KrahwinkelSource, SourceHost: "depot.krahwinkel-it.nl"),
            ],
            RemoteChangedProjectIds = new HashSet<string>(StringComparer.Ordinal) { flow.Id },
            BoundProjectLocations = bound,
            HiddenSharedProjectLocations = withHidden
                ? new Dictionary<string, SharedProjectLocation>(StringComparer.Ordinal) { ["hidden"] = _AtSynvolution() }
                : new Dictionary<string, SharedProjectLocation>(StringComparer.Ordinal),
        };

        cockpit.Projects.StageSnapshot(snapshot);
        _StageRunning(cockpit, flow, 1);
        _StageRunning(cockpit, cockpitProject, 3);

        // The mockup's own selection at 1100: Synvolution Flow, the row with the most to say.
        cockpit.Projects.SelectedListItem = cockpit.Projects.ProjectSections
            .SelectMany(section => section.Items)
            .First(row => row.Id == flow.Id);
    }

    private static void _StageTwoConnectionsWithHidden(CockpitViewModel cockpit) => _StageTwoConnections(cockpit, withHidden: true);

    private static void _StageTwoConnectionsSelecting(CockpitViewModel cockpit, string name)
    {
        _StageTwoConnections(cockpit);
        cockpit.Projects.SelectedListItem = cockpit.Projects.ProjectSections
            .SelectMany(section => section.Items)
            .First(row => row.Name == name);
    }

    private static void _StageRunning(CockpitViewModel cockpit, Project project, int count)
    {
        if (cockpit.Workspaces.Active is not { } workspace)
        {
            throw new InvalidOperationException("The Projects workspace scene has no active workspace to run its sessions in.");
        }

        for (var index = 0; index < count; index++)
        {
            cockpit.Sessions.Add(new SessionViewModel
            {
                Title = $"{project.Name} {index + 1}",
                ProjectId = project.Id,
                WorkspaceId = workspace.Id,
                SessionStatus = SessionStatus.Busy,
                ActiveProfileLabel = project.DefaultProfileLabel,
            });
        }
    }

    private static void _StagePartialTeamProjects(CockpitViewModel cockpit) =>
        cockpit.Projects.StageSnapshot(ProjectCatalogSnapshot.Empty with
        {
            Sources = _sources,
            SharedProjectGroups =
            [
                new("Depot — Synvolution",
                [
                    new($"{SynvolutionSource}:release-guide", "Release guide", "The current release checklist.", "Editor", _synvolution),
                    new($"{SynvolutionSource}:startpage", "startpage", Role: "Owner", Organization: _synvolution,
                        Unavailable: "No Cockpit project definition yet."),
                ], Error: null, SourceKey: SynvolutionSource, SourceHost: "depot.synvolution-software.nl"),
            ],
        });

    private static void _StageUnreachableServer(CockpitViewModel cockpit) =>
        cockpit.Projects.StageSnapshot(ProjectCatalogSnapshot.Empty with
        {
            Sources = _sources,
            Settings = ProjectSettings.Empty.WithProject(_Project("EVE Workbench", "Claude · Sonnet", TimeSpan.FromDays(4),
                "Industry and market calculator for manufacturing chains.")),
            SharedProjectGroups =
            [
                new("Depot — Synvolution",
                [
                    new($"{SynvolutionSource}:api-gateway", "API Gateway", "Routing, rate limits and API keys in front of every public service.", "Editor", _synvolution),
                ], Error: null, SourceKey: SynvolutionSource, SourceHost: "depot.synvolution-software.nl"),
                new("Depot — Krahwinkel IT", [], "Couldn't reach depot.krahwinkel-it.nl — check your connection, then try again.",
                    SourceKey: KrahwinkelSource, SourceHost: "depot.krahwinkel-it.nl"),
            ],
        });

    private static void _StageLoading(CockpitViewModel cockpit)
    {
        _StageTwoConnections(cockpit);
        cockpit.Projects.IsRefreshingSharedProjects = true;
    }

    private static void _StageStaleServer(CockpitViewModel cockpit) =>
        cockpit.Projects.StageSnapshot(ProjectCatalogSnapshot.Empty with
        {
            Sources = _sources,
            SharedProjectGroups =
            [
                new("Depot — Synvolution",
                [
                    new($"{SynvolutionSource}:api-gateway", "API Gateway", "Routing, rate limits and API keys in front of every public service.", "Editor", _synvolution),
                ], Error: null, SourceKey: SynvolutionSource, SourceHost: "depot.synvolution-software.nl",
                    Notice: "Couldn't reach Depot — showing the last known list · just now"),
            ],
        });

    private static void _StageLongProjectText(CockpitViewModel cockpit)
    {
        var noProfile = _Project("Research and discovery notes for the next workspace migration", profile: null, openedAgo: null,
            "A deliberately long project name and description that must wrap instead of hiding the next action on a narrow workspace.");
        var running = _Project("Release train", "personal", TimeSpan.FromHours(3), "A project with an active session and a scheduled job.") with
        {
            Jobs = [new ProjectJob("Check every migration dependency before release", "Reads configuration and writes a release report.")],
        };

        cockpit.Projects.StageSnapshot(ProjectCatalogSnapshot.Empty with
        {
            Sources = _sources,
            Settings = ProjectSettings.Empty with { Projects = [noProfile, running] },
            SharedProjectGroups =
            [
                new("Depot — Research",
                [
                    new($"{SynvolutionSource}:research", "A very long shared-project name that needs to stay legible on a narrow workspace",
                        "A deliberately long description checks that a shared project row trims the text without pushing its action out of view.",
                        "Viewer", _synvolution),
                ], Error: null, SourceKey: SynvolutionSource, SourceHost: "depot.synvolution-software.nl"),
            ],
        });
        _StageRunning(cockpit, running, 1);
    }
}
