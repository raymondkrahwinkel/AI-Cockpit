using NSubstitute;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The projects manager (AC-161). It owns the persisting the editor deliberately does not, so what it writes
/// after each add/edit/remove is the whole feature's source of truth.
/// </summary>
public class ProjectsViewModelTests
{
    private static (ProjectsViewModel ViewModel, IProjectStore Store, ISessionDialogService Dialogs) Build(
        params Project[] saved)
    {
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings { Projects = saved });

        var dialogs = Substitute.For<ISessionDialogService>();
        return (new ProjectsViewModel(store, dialogs), store, dialogs);
    }

    [Fact]
    public async Task LoadAsync_PublishesTheSavedProjects()
    {
        var (viewModel, _, _) = Build(Project.Create("Cockpit"), Project.Create("Depot"));

        await viewModel.LoadAsync();

        Assert.Equal(new[] { "Cockpit", "Depot" }, viewModel.Projects.Select(project => project.Name));
        Assert.True(viewModel.HasProjects);
    }

    [Fact]
    public async Task RecentProjects_LeadWithWhatWasWorkedOnLast()
    {
        var never = Project.Create("Archive");
        var older = Project.Create("Depot") with { LastOpenedAt = DateTimeOffset.Now.AddDays(-3) };
        var newest = Project.Create("Cockpit") with { LastOpenedAt = DateTimeOffset.Now.AddMinutes(-5) };
        var (viewModel, _, _) = Build(never, older, newest);

        await viewModel.LoadAsync();

        Assert.Equal(new[] { "Cockpit", "Depot", "Archive" }, viewModel.RecentProjects.Select(project => project.Name));
        // The manager keeps the operator's own order — re-sorting it under them on every start is its own chaos.
        Assert.Equal(new[] { "Archive", "Depot", "Cockpit" }, viewModel.Projects.Select(project => project.Name));
        Assert.Equal("Cockpit", viewModel.MostRecentProject?.Name);
        Assert.Equal(2, viewModel.OpenedProjectCount);
    }

    [Fact]
    public async Task SidebarProjects_AreTheRecentFew_WithTheRestOneClickAway()
    {
        // The sidebar strip is for reaching what you are busy with; a list that grows with every project turns it
        // back into a menu, so it holds the recent handful and says where the others are.
        var saved = Enumerable.Range(1, 7)
            .Select(index => Project.Create($"Project {index}") with { LastOpenedAt = DateTimeOffset.Now.AddMinutes(-index) })
            .ToArray();
        var (viewModel, _, _) = Build(saved);

        await viewModel.LoadAsync();

        Assert.Equal(5, System.Linq.Enumerable.Count(viewModel.SidebarProjects));
        Assert.Equal(new[] { "Project 1", "Project 2", "Project 3", "Project 4", "Project 5" }, viewModel.SidebarProjects.Select(project => project.Name));
        Assert.True(viewModel.HasMoreThanSidebarShows);
    }

    [Fact]
    public async Task WithFewProjects_TheSidebarShowsThemAll_AndSaysNothingAboutMore()
    {
        var (viewModel, _, _) = Build(Project.Create("Cockpit"), Project.Create("Depot"));

        await viewModel.LoadAsync();

        Assert.Equal(2, System.Linq.Enumerable.Count(viewModel.SidebarProjects));
        Assert.False(viewModel.HasMoreThanSidebarShows);
    }

    [Fact]
    public async Task MarkOpened_PersistsWhenItWasWorkedOn()
    {
        var project = Project.Create("Cockpit");
        var (viewModel, store, _) = Build(project);
        await viewModel.LoadAsync();
        var openedAt = DateTimeOffset.Now;

        await viewModel.MarkOpenedAsync(project, openedAt);

        await store.Received(1).SaveAsync(
            Arg.Is<ProjectSettings>(settings => settings.Projects[0].LastOpenedAt == openedAt),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkOpened_ForAProjectRemovedMeanwhile_WritesNothing()
    {
        var (viewModel, store, _) = Build();
        await viewModel.LoadAsync();

        await viewModel.MarkOpenedAsync(Project.Create("Gone"), DateTimeOffset.Now);

        await store.DidNotReceive().SaveAsync(Arg.Any<ProjectSettings>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddProject_WhenTheEditorReturnsOne_PersistsAndSelectsIt()
    {
        var (viewModel, store, dialogs) = Build();
        var created = Project.Create("Cockpit");
        dialogs.ShowProjectDialogAsync(null).Returns(created);
        await viewModel.LoadAsync();

        await viewModel.AddProjectCommand.ExecuteAsync(null);

        await store.Received(1).SaveAsync(
            Arg.Is<ProjectSettings>(settings => settings.Projects.Count == 1 && settings.Projects[0].Id == created.Id),
            Arg.Any<CancellationToken>());
        Assert.Equal(created.Id, viewModel.SelectedProject?.Id);
    }

    [Fact]
    public async Task AddProject_WhenTheEditorIsCancelled_WritesNothing()
    {
        var (viewModel, store, dialogs) = Build();
        dialogs.ShowProjectDialogAsync(null).Returns((Project?)null);
        await viewModel.LoadAsync();

        await viewModel.AddProjectCommand.ExecuteAsync(null);

        await store.DidNotReceive().SaveAsync(Arg.Any<ProjectSettings>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EditProject_PersistsTheEditedProjectUnderTheSameId()
    {
        var project = Project.Create("Cockpit");
        var (viewModel, store, dialogs) = Build(project);
        dialogs.ShowProjectDialogAsync(Arg.Any<Project?>()).Returns(project with { Name = "AI-Cockpit" });
        await viewModel.LoadAsync();
        viewModel.SelectedProject = viewModel.Projects[0];

        await viewModel.EditProjectCommand.ExecuteAsync(null);

        await store.Received(1).SaveAsync(
            Arg.Is<ProjectSettings>(settings => settings.Projects.Count == 1 && settings.Projects[0].Name == "AI-Cockpit"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveProject_OnlyAfterConfirmation()
    {
        var project = Project.Create("Cockpit");
        var (viewModel, store, dialogs) = Build(project);
        dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        await viewModel.LoadAsync();
        viewModel.SelectedProject = viewModel.Projects[0];

        await viewModel.RemoveProjectCommand.ExecuteAsync(null);

        await store.DidNotReceive().SaveAsync(Arg.Any<ProjectSettings>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveProject_Confirmed_PersistsTheShorterList()
    {
        var removed = Project.Create("Cockpit");
        var kept = Project.Create("Depot");
        var (viewModel, store, dialogs) = Build(removed, kept);
        dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        await viewModel.LoadAsync();
        viewModel.SelectedProject = viewModel.Projects[0];

        await viewModel.RemoveProjectCommand.ExecuteAsync(null);

        await store.Received(1).SaveAsync(
            Arg.Is<ProjectSettings>(settings => settings.Projects.Count == 1 && settings.Projects[0].Id == kept.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EditAndRemove_AreUnavailableWithoutASelection()
    {
        var (viewModel, _, _) = Build(Project.Create("Cockpit"));
        await viewModel.LoadAsync();

        Assert.False(viewModel.EditProjectCommand.CanExecute(null));
        Assert.False(viewModel.RemoveProjectCommand.CanExecute(null));

        viewModel.SelectedProject = viewModel.Projects[0];

        Assert.True(viewModel.EditProjectCommand.CanExecute(null));
        Assert.True(viewModel.RemoveProjectCommand.CanExecute(null));
    }

    /// <summary>Reloading must not silently move the operator's selection to a different project.</summary>
    [Fact]
    public async Task LoadAsync_KeepsTheSelectionWhenTheProjectIsStillThere()
    {
        var project = Project.Create("Cockpit");
        var (viewModel, _, _) = Build(project, Project.Create("Depot"));
        await viewModel.LoadAsync();
        viewModel.SelectedProject = viewModel.Projects[1];

        await viewModel.LoadAsync();

        Assert.Equal("Depot", viewModel.SelectedProject?.Name);
    }

    // AC-245: the shared-project catalog — grouping, filtering an already-bound or hidden project out, one
    // source's failure not costing the others, and the AC-604 ownership claim this consumes it through.

    private static (ProjectsViewModel ViewModel, IProjectOwnershipRegistry Ownership) BuildWithSharedSources(
        IReadOnlyList<ISharedProjectSource> sources, params Project[] saved) =>
        BuildWithSharedSources(sources, dialogs: null, out _, saved);

    private static (ProjectsViewModel ViewModel, IProjectOwnershipRegistry Ownership) BuildWithSharedSources(
        IReadOnlyList<ISharedProjectSource> sources, ISessionDialogService? dialogs, out IProjectStore store, params Project[] saved)
    {
        store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings { Projects = saved });

        var ownership = new ProjectOwnershipRegistry();
        var registry = new _FakeSharedProjectSourceRegistry(sources);
        var viewModel = new ProjectsViewModel(store, dialogs, ownership: ownership, sharedSources: registry);
        return (viewModel, ownership);
    }

    private static (ProjectsViewModel ViewModel, IProjectOwnershipRegistry Ownership) BuildWithSharedSourcesAndHidden(
        IReadOnlyList<ISharedProjectSource> sources, IReadOnlyList<string> hiddenIds)
    {
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings { HiddenSharedProjectIds = hiddenIds });

        var ownership = new ProjectOwnershipRegistry();
        var registry = new _FakeSharedProjectSourceRegistry(sources);
        var viewModel = new ProjectsViewModel(store, dialogs: null, ownership: ownership, sharedSources: registry);
        return (viewModel, ownership);
    }

    [Fact]
    public async Task LoadSharedProjectsAsync_PublishesOneGroupPerSource()
    {
        var source = new _FakeSharedProjectSource("Depot — Work", SharedProjectListResult.Success(
        [
            new SharedProject("depot:one", "One"),
            new SharedProject("depot:two", "Two"),
        ]));
        var (viewModel, _) = BuildWithSharedSources([source]);

        await viewModel.LoadSharedProjectsAsync();

        var group = Assert.Single(viewModel.SharedProjectGroups);
        Assert.Equal("Depot — Work", group.SourceName);
        Assert.Equal(["One", "Two"], group.Projects.Select(project => project.Name));
        Assert.True(viewModel.HasSharedProjects);
    }

    [Fact]
    public async Task LoadSharedProjectsAsync_ExcludesAProjectAlreadyBoundToALocalProject()
    {
        var bound = Project.Create("Cockpit") with { MemoryRef = "depot:one" };
        var source = new _FakeSharedProjectSource("Depot — Work", SharedProjectListResult.Success(
        [
            new SharedProject("depot:one", "One"),
            new SharedProject("depot:two", "Two"),
        ]));
        var (viewModel, _) = BuildWithSharedSources([source], bound);

        await viewModel.LoadAsync();
        await viewModel.SharedProjectsLoadTask;

        var group = Assert.Single(viewModel.SharedProjectGroups);
        Assert.Equal(["Two"], group.Projects.Select(project => project.Name));
    }

    [Fact]
    public async Task LoadSharedProjectsAsync_ExcludesAProjectHiddenOnThisMachine()
    {
        var source = new _FakeSharedProjectSource("Depot — Work", SharedProjectListResult.Success(
        [
            new SharedProject("depot:one", "One"),
            new SharedProject("depot:two", "Two"),
        ]));
        var (viewModel, _) = BuildWithSharedSourcesAndHidden([source], ["depot:one"]);

        await viewModel.LoadAsync();
        await viewModel.SharedProjectsLoadTask;

        var group = Assert.Single(viewModel.SharedProjectGroups);
        Assert.Equal(["Two"], group.Projects.Select(project => project.Name));
    }

    [Fact]
    public async Task LoadSharedProjectsAsync_EmptySuccessfulGroup_IsLeftOutEntirely()
    {
        var source = new _FakeSharedProjectSource("Depot — Work", SharedProjectListResult.Success([]));
        var (viewModel, _) = BuildWithSharedSources([source]);

        await viewModel.LoadSharedProjectsAsync();

        Assert.Empty(viewModel.SharedProjectGroups);
        Assert.False(viewModel.HasSharedProjects);
    }

    [Fact]
    public async Task LoadSharedProjectsAsync_OneSourceFailing_LeavesTheOthersIntact()
    {
        var broken = new _FakeSharedProjectSource("Depot — Broken", SharedProjectListResult.Failed("not signed in"));
        var healthy = new _FakeSharedProjectSource("Depot — Work", SharedProjectListResult.Success([new SharedProject("depot:one", "One")]));
        var (viewModel, _) = BuildWithSharedSources([broken, healthy]);

        await viewModel.LoadSharedProjectsAsync();

        Assert.Equal(2, viewModel.SharedProjectGroups.Count);
        var brokenGroup = viewModel.SharedProjectGroups.Single(group => group.SourceName == "Depot — Broken");
        Assert.True(brokenGroup.HasError);
        Assert.Equal("not signed in", brokenGroup.Error);
        Assert.Empty(brokenGroup.Projects);

        var healthyGroup = viewModel.SharedProjectGroups.Single(group => group.SourceName == "Depot — Work");
        Assert.False(healthyGroup.HasError);
        Assert.Equal(["One"], healthyGroup.Projects.Select(project => project.Name));
    }

    /// <summary>
    /// Adversarial: a source that throws instead of reporting a failure through <see cref="SharedProjectListResult.Failed"/>
    /// as its own contract asks (a bug in a third-party plugin, say) must still degrade to a named failure rather
    /// than crashing the whole workspace load.
    /// </summary>
    [Fact]
    public async Task LoadSharedProjectsAsync_ASourceThatThrows_DegradesToAFailedGroup()
    {
        var throwing = new _FakeSharedProjectSource("Depot — Buggy", exception: new InvalidOperationException("boom"));
        var (viewModel, _) = BuildWithSharedSources([throwing]);

        await viewModel.LoadSharedProjectsAsync();

        var group = Assert.Single(viewModel.SharedProjectGroups);
        Assert.True(group.HasError);
        Assert.Contains("boom", group.Error);
    }

    [Fact]
    public async Task LoadSharedProjectsAsync_ASourceThatNeverCompletes_TimesOutRatherThanHangingForever()
    {
        ProjectsViewModel.SharedProjectSourceTimeout = TimeSpan.FromMilliseconds(20);
        try
        {
            var hanging = new _FakeSharedProjectSource("Depot — Slow", neverCompletes: true);
            var (viewModel, _) = BuildWithSharedSources([hanging]);

            await viewModel.LoadSharedProjectsAsync();

            var group = Assert.Single(viewModel.SharedProjectGroups);
            Assert.True(group.HasError);
        }
        finally
        {
            ProjectsViewModel.SharedProjectSourceTimeout = TimeSpan.FromSeconds(10);
        }
    }

    [Fact]
    public async Task LoadSharedProjectsAsync_ClaimsOwnershipForALocalProjectAlreadyBoundToASharedOne()
    {
        var bound = Project.Create("Cockpit") with { MemoryRef = "depot:one" };
        var source = new _FakeSharedProjectSource("Depot — Work", SharedProjectListResult.Success([new SharedProject("depot:one", "One")]));
        var (viewModel, ownership) = BuildWithSharedSources([source], bound);

        await viewModel.LoadAsync();
        await viewModel.SharedProjectsLoadTask;

        var resolved = ownership.Resolve(bound.Id);
        Assert.NotNull(resolved);
        Assert.Equal("Depot — Work", resolved![HostProjectField.Name]?.SourceName);
        // A claimed field is locked host-side today regardless of what IsEditable says (no write-back yet, AC-247)
        // — this reconciliation must not claim editability it cannot honour.
        Assert.False(resolved[HostProjectField.Name]?.IsEditable);
    }

    [Fact]
    public async Task LoadSharedProjectsAsync_NoUnboundProjectOnAnySource_ClaimsNothing()
    {
        var unbound = Project.Create("Cockpit");
        var source = new _FakeSharedProjectSource("Depot — Work", SharedProjectListResult.Success([new SharedProject("depot:one", "One")]));
        var (viewModel, ownership) = BuildWithSharedSources([source], unbound);

        await viewModel.LoadAsync();
        await viewModel.SharedProjectsLoadTask;

        Assert.Null(ownership.Resolve(unbound.Id));
    }

    [Fact]
    public async Task LoadAsync_DoesNotAwaitTheSharedProjectsLoad()
    {
        // LoadAsync is awaited by SessionDialogService.ShowProjectsDialogAsync before the workspace window is even
        // constructed — a source that never answers must not hold that open forever.
        var hanging = new _FakeSharedProjectSource("Depot — Slow", neverCompletes: true);
        var (viewModel, _) = BuildWithSharedSources([hanging]);

        var loadTask = viewModel.LoadAsync();
        var completed = await Task.WhenAny(loadTask, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(loadTask, completed);
    }

    // AC-246: the "Finish setting up…" bind step. FinishSettingUpAsync itself never talks to Depot — it only finds
    // the right ISharedProjectSource by SharedProject.Id's own scheme prefix and hands off to ISessionDialogService,
    // so these tests fake the dialog rather than the plugin read (that is DepotSharedProjectSourcePrepareBindingTests'
    // and SharedProjectBindingDialogViewModelTests' job).

    [Fact]
    public async Task FinishSettingUpAsync_TheDialogReturnsAProject_PersistsItAndReloadsSharedProjects()
    {
        var shared = new SharedProject("depot:one", "One");
        var source = new _FakeSharedProjectSource("depot", SharedProjectListResult.Success([shared]));
        var dialogs = Substitute.For<ISessionDialogService>();
        var bound = Project.Create("One") with { MemoryRef = "depot:one", DefaultProfileLabel = "Zyra" };
        dialogs.ShowSharedProjectBindingDialogAsync(shared, "depot", source).Returns(bound);
        var (viewModel, _) = BuildWithSharedSources([source], dialogs, out var store);
        await viewModel.LoadAsync();
        await viewModel.SharedProjectsLoadTask;
        Assert.Single(viewModel.SharedProjectGroups.Single().Projects); // the row is there before binding

        await viewModel.FinishSettingUpCommand.ExecuteAsync(shared);

        await store.Received(1).SaveAsync(
            Arg.Is<ProjectSettings>(settings => settings.Projects.Any(project => project.Id == bound.Id)),
            Arg.Any<CancellationToken>());
        // Bound now — boundIds picks up its own Memory row on the very next reload this call already triggered,
        // so the row must not still be sitting under "Shared via …" for the operator to bind a second time by
        // mistake ("dezelfde binding twee keer koppelen", AC-246 harness).
        Assert.Empty(viewModel.SharedProjectGroups);
    }

    [Fact]
    public async Task FinishSettingUpAsync_TheOperatorCancels_WritesNothing()
    {
        var shared = new SharedProject("depot:one", "One");
        var source = new _FakeSharedProjectSource("depot", SharedProjectListResult.Success([shared]));
        var dialogs = Substitute.For<ISessionDialogService>();
        dialogs.ShowSharedProjectBindingDialogAsync(shared, "depot", source).Returns((Project?)null);
        var (viewModel, _) = BuildWithSharedSources([source], dialogs, out var store);
        await viewModel.LoadAsync();
        await viewModel.SharedProjectsLoadTask;

        await viewModel.FinishSettingUpCommand.ExecuteAsync(shared);

        await store.DidNotReceive().SaveAsync(Arg.Any<ProjectSettings>(), Arg.Any<CancellationToken>());
        Assert.Single(viewModel.SharedProjectGroups.Single().Projects); // still there, unbound
    }

    [Fact]
    public async Task FinishSettingUpAsync_NoMatchingSourceRegisteredAnyMore_DoesNothingRatherThanThrowing()
    {
        // "koppelen terwijl de Depot-verbinding wegvalt" (AC-246 harness): the connection was removed between the
        // list rendering and the click — SharedProjectGroups can still show a stale row from before that reload.
        var shared = new SharedProject("depot:one", "One");
        var dialogs = Substitute.For<ISessionDialogService>();
        var (viewModel, _) = BuildWithSharedSources([], dialogs, out var store);
        await viewModel.LoadAsync();

        await viewModel.FinishSettingUpCommand.ExecuteAsync(shared);

        await store.DidNotReceive().SaveAsync(Arg.Any<ProjectSettings>(), Arg.Any<CancellationToken>());
        await dialogs.DidNotReceive().ShowSharedProjectBindingDialogAsync(Arg.Any<SharedProject>(), Arg.Any<string>(), Arg.Any<ISharedProjectSource>());
    }

    private sealed class _FakeSharedProjectSourceRegistry(IReadOnlyList<ISharedProjectSource> sources) : ISharedProjectSourceRegistry
    {
        public IReadOnlyList<ISharedProjectSource> Sources => sources;

        public bool Register(ISharedProjectSource source) => true;

        public void Remove(string key)
        {
        }
    }

    private sealed class _FakeSharedProjectSource : ISharedProjectSource
    {
        private readonly SharedProjectListResult? _result;
        private readonly Exception? _exception;
        private readonly bool _neverCompletes;

        public _FakeSharedProjectSource(string sourceName, SharedProjectListResult? result = null, Exception? exception = null, bool neverCompletes = false)
        {
            SourceName = sourceName;
            _result = result;
            _exception = exception;
            _neverCompletes = neverCompletes;
        }

        public string Key => SourceName;

        public string SourceName { get; }

        public async Task<SharedProjectListResult> ListAsync(CancellationToken cancellationToken)
        {
            if (_neverCompletes)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (_exception is not null)
            {
                throw _exception;
            }

            return _result!;
        }

        // AC-246: not exercised by any test in this file (none of them call ProjectsViewModel.FinishSettingUpAsync)
        // — a fixed failure is enough to satisfy the interface without a fake result nothing here reads.
        public Task<SharedProjectBindingResult> PrepareBindingAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(SharedProjectBindingResult.Failed("not implemented by this fake"));
    }
}
