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

    // AC-618: the category groups the list actually renders, and the per-card origin badge that replaces AC-245's
    // separate "On this machine" heading.

    [Fact]
    public async Task ProjectCategoryGroups_NoProjectHasACategory_IsOneHeaderlessGroupWithEveryProject()
    {
        var (viewModel, _, _) = Build(Project.Create("Cockpit"), Project.Create("Depot"));

        await viewModel.LoadAsync();

        var group = Assert.Single(viewModel.ProjectCategoryGroups);
        Assert.False(group.HasHeader);
        Assert.Null(group.CategoryName);
        Assert.Equal(["Cockpit", "Depot"], group.Cards.Select(card => card.Project.Name));
    }

    [Fact]
    public async Task ProjectCategoryGroups_GroupsByCategory_InCategoryOrderNotAlphabetical()
    {
        var work = Project.Create("Cockpit") with { Category = "Werk" };
        var personal = Project.Create("Home lab") with { Category = "Privé" };
        var plain = Project.Create("Scratch");
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new ProjectSettings { Projects = [work, personal, plain], CategoryOrder = ["Werk", "Privé"] });
        var viewModel = new ProjectsViewModel(store, dialogs: null);

        await viewModel.LoadAsync();

        Assert.Equal(["Werk", "Privé", "Uncategorized"], viewModel.ProjectCategoryGroups.Select(group => group.CategoryName));
        Assert.Equal(["Cockpit"], viewModel.ProjectCategoryGroups[0].Cards.Select(card => card.Project.Name));
        Assert.Equal(["Home lab"], viewModel.ProjectCategoryGroups[1].Cards.Select(card => card.Project.Name));
        Assert.Equal(["Scratch"], viewModel.ProjectCategoryGroups[2].Cards.Select(card => card.Project.Name));
        Assert.True(viewModel.ProjectCategoryGroups[2].HasHeader);
    }

    [Fact]
    public async Task ProjectCategoryGroups_MatchesCategoryCaseInsensitively()
    {
        var lower = Project.Create("A") with { Category = "werk" };
        var upper = Project.Create("B") with { Category = "WERK" };
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new ProjectSettings { Projects = [lower, upper], CategoryOrder = ["Werk"] });
        var viewModel = new ProjectsViewModel(store, dialogs: null);

        await viewModel.LoadAsync();

        var group = viewModel.ProjectCategoryGroups.Single(g => g.CategoryName == "Werk");
        Assert.Equal(["A", "B"], group.Cards.Select(card => card.Project.Name));
    }

    [Fact]
    public async Task ProjectCategoryGroups_UncategorizedGroup_NeverDisappearsEvenWhenEmpty()
    {
        var work = Project.Create("Cockpit") with { Category = "Werk" };
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new ProjectSettings { Projects = [work], CategoryOrder = ["Werk"] });
        var viewModel = new ProjectsViewModel(store, dialogs: null);

        await viewModel.LoadAsync();

        var uncategorized = viewModel.ProjectCategoryGroups.Last();
        Assert.Equal("Uncategorized", uncategorized.CategoryName);
        Assert.Empty(uncategorized.Cards);
    }

    /// <summary>
    /// Reproduces the ordering hazard directly: <c>_PersistAsync</c> assigns the settings it was handed, not the
    /// normalized settings <c>IProjectStore.SaveAsync</c> actually wrote — a category typed for the very first time
    /// on this save has no <c>CategoryOrder</c> entry yet in the in-memory <c>_settings</c>. The group must still
    /// show (reading a locally normalized view rather than trusting <c>_settings.CategoryOrder</c> verbatim) —
    /// otherwise the project would render in neither the new category's group nor "Uncategorized".
    /// </summary>
    [Fact]
    public async Task ProjectCategoryGroups_ANewlyTypedCategory_ShowsImmediatelyEvenBeforeCategoryOrderCatchesUp()
    {
        var (viewModel, store, dialogs) = Build();
        var created = Project.Create("Cockpit") with { Category = "Werk" };
        dialogs.ShowProjectDialogAsync(null).Returns(created);
        await viewModel.LoadAsync();

        await viewModel.AddProjectCommand.ExecuteAsync(null);

        var group = viewModel.ProjectCategoryGroups.Single(g => g.CategoryName == "Werk");
        Assert.Equal(["Cockpit"], group.Cards.Select(card => card.Project.Name));
    }

    [Fact]
    public async Task ProjectCardViewModel_UnclaimedProject_ShowsTheLocalBadge()
    {
        var (viewModel, _, _) = Build(Project.Create("Cockpit"));

        await viewModel.LoadAsync();

        Assert.Equal("● This machine", Assert.Single(viewModel.ProjectCategoryGroups.Single().Cards).OriginBadge);
    }

    [Fact]
    public async Task ProjectCardViewModel_AProjectBoundToASharedSource_ShowsItsConnectionBadge()
    {
        // The claim itself only happens inside the background LoadSharedProjectsAsync call (_ClaimBoundProjects) —
        // this also proves that call's own AC-618 refresh actually reaches ProjectCategoryGroups, not only
        // SharedProjectGroups, once it resolves.
        var bound = Project.Create("Cockpit") with { MemoryRef = "depot:one" };
        var source = new _FakeSharedProjectSource("Depot — Work", SharedProjectListResult.Success([new SharedProject("depot:one", "One")]));
        var (viewModel, _) = BuildWithSharedSources([source], bound);

        await viewModel.LoadAsync();
        await viewModel.SharedProjectsLoadTask;

        var card = Assert.Single(viewModel.ProjectCategoryGroups.Single().Cards);
        Assert.Equal("◆ Depot — Work", card.OriginBadge);
    }

    [Fact]
    public async Task ProjectCardViewModel_SelectingAProject_MarksItsCardAndClearsTheOldOne()
    {
        var first = Project.Create("Cockpit");
        var second = Project.Create("Depot");
        var (viewModel, _, _) = Build(first, second);
        await viewModel.LoadAsync();
        var cards = viewModel.ProjectCategoryGroups.Single().Cards;

        viewModel.SelectedProject = first;
        Assert.True(cards.Single(card => card.Project.Id == first.Id).IsSelected);
        Assert.False(cards.Single(card => card.Project.Id == second.Id).IsSelected);

        viewModel.SelectedProject = second;
        Assert.False(cards.Single(card => card.Project.Id == first.Id).IsSelected);
        Assert.True(cards.Single(card => card.Project.Id == second.Id).IsSelected);
    }

    [Fact]
    public async Task ClaimBoundProjects_ARoleThatCanWriteBack_ClaimsEveryFieldEditableExceptLogo()
    {
        // AC-247: SharedProject.CanWriteBack (the source's own Editor/Owner check) drives IsEditable — Logo stays
        // an override to locked regardless, since no artifact-upload path exists yet for writing it back.
        var bound = Project.Create("Cockpit") with { MemoryRef = "depot:one" };
        var source = new _FakeSharedProjectSource("Depot — Work", SharedProjectListResult.Success(
        [
            new SharedProject("depot:one", "One") { CanWriteBack = true },
        ]));
        var (viewModel, ownership) = BuildWithSharedSources([source], bound);

        await viewModel.LoadAsync();
        await viewModel.SharedProjectsLoadTask;

        var claim = ownership.Resolve(bound.Id)!;
        Assert.True(claim[HostProjectField.Name]!.IsEditable);
        Assert.True(claim[HostProjectField.Description]!.IsEditable);
        Assert.True(claim[HostProjectField.Behavior]!.IsEditable);
        Assert.True(claim[HostProjectField.McpOverlay]!.IsEditable);
        Assert.True(claim[HostProjectField.WorktreeSwitch]!.IsEditable);
        Assert.False(claim[HostProjectField.Logo]!.IsEditable, "no write-back path exists yet for a shared logo");
    }

    [Fact]
    public async Task ClaimBoundProjects_ARoleThatCannotWriteBack_ClaimsEveryFieldLocked()
    {
        var bound = Project.Create("Cockpit") with { MemoryRef = "depot:one" };
        var source = new _FakeSharedProjectSource("Depot — Work", SharedProjectListResult.Success(
        [
            new SharedProject("depot:one", "One") { CanWriteBack = false },
        ]));
        var (viewModel, ownership) = BuildWithSharedSources([source], bound);

        await viewModel.LoadAsync();
        await viewModel.SharedProjectsLoadTask;

        var claim = ownership.Resolve(bound.Id)!;
        Assert.False(claim[HostProjectField.Name]!.IsEditable);
        Assert.False(claim[HostProjectField.Logo]!.IsEditable);
    }

    /// <summary>Meet table edge case: editing the last project in a category out of it removes that category's group entirely — it does not linger as an empty one the way "Uncategorized" does.</summary>
    [Fact]
    public async Task ProjectCategoryGroups_TheLastProjectInACategory_EditedToDropIt_RemovesTheGroup()
    {
        var project = Project.Create("Cockpit") with { Category = "Werk" };
        var (viewModel, _, dialogs) = Build(project);
        await viewModel.LoadAsync();
        viewModel.SelectedProject = viewModel.Projects[0];
        dialogs.ShowProjectDialogAsync(Arg.Any<Project?>()).Returns(project with { Category = null });

        await viewModel.EditProjectCommand.ExecuteAsync(null);

        Assert.DoesNotContain(viewModel.ProjectCategoryGroups, group => group.CategoryName == "Werk");
        var uncategorized = viewModel.ProjectCategoryGroups.Single();
        Assert.Null(uncategorized.CategoryName);
    }

    /// <summary>Meet table edge case: a shared project with no category sits beside a categorized local one — same "Uncategorized" group, badges independent of category.</summary>
    [Fact]
    public async Task ProjectCategoryGroups_ASharedProjectWithoutACategory_SitsInUncategorized_NextToACategorizedLocalOne()
    {
        var categorizedLocal = Project.Create("Cockpit") with { Category = "Werk" };
        var uncategorizedBound = Project.Create("Onboarding flow") with { MemoryRef = "depot:one" };
        var source = new _FakeSharedProjectSource("Depot — Work", SharedProjectListResult.Success([new SharedProject("depot:one", "One")]));
        var (viewModel, _) = BuildWithSharedSources([source], categorizedLocal, uncategorizedBound);

        await viewModel.LoadAsync();
        await viewModel.SharedProjectsLoadTask;

        var uncategorized = viewModel.ProjectCategoryGroups.Single(group => group.CategoryName == "Uncategorized");
        var card = Assert.Single(uncategorized.Cards);
        Assert.Equal("Onboarding flow", card.Project.Name);
        Assert.Equal("◆ Depot — Work", card.OriginBadge);

        var werk = viewModel.ProjectCategoryGroups.Single(group => group.CategoryName == "Werk");
        Assert.Equal("Cockpit", Assert.Single(werk.Cards).Project.Name);
    }

    // AC-620: ToggleSharingAsync(Project) is the launcher's own parameterized entry point (no selection to read,
    // unlike the RelayCommand ProjectsDialog uses) — same either/or the selection-based command wraps.
    [Fact]
    public async Task ToggleSharingAsync_LocalProject_OpensTheShareDialogAndPersistsTheReturnedProject()
    {
        var local = Project.Create("PayrollProcessor");
        // publishResult is only what makes CanPublish true here — the dialog itself (mocked below) is what would
        // call PublishAsync in the real app; ProjectsViewModel never calls it directly.
        var source = new _FakeSharedProjectSource(
            "Depot — Work", SharedProjectListResult.Success([]), publishResult: SharedProjectPublishResult.Success("depot:new-project"));
        var dialogs = Substitute.For<ISessionDialogService>();
        var (viewModel, _) = BuildWithSharedSources([source], dialogs, out var store, local);
        await viewModel.LoadAsync();

        var bound = local with { Resources = [new ProjectResource("depot:new-project", ProjectResourceRole.Memory)] };
        dialogs.ShowShareProjectDialogAsync(local, Arg.Is<IReadOnlyList<ISharedProjectSource>>(sources => sources.Contains(source)))
            .Returns(bound);

        await viewModel.ToggleSharingAsync(local);

        await store.Received(1).SaveAsync(Arg.Is<ProjectSettings>(settings =>
            settings.Projects.Single().Resources.Any(resource => resource.Reference == "depot:new-project")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ToggleSharingAsync_LocalProject_NoPublishCapableSource_NeverOpensADialog()
    {
        var local = Project.Create("PayrollProcessor");
        // CanPublish is false here (_FakeSharedProjectSource with no publishResult) — the same shape a read-only
        // ISharedProjectSource (one that only ever implemented ListAsync/PrepareBindingAsync) already has.
        var source = new _FakeSharedProjectSource("Depot — Work", SharedProjectListResult.Success([]));
        var dialogs = Substitute.For<ISessionDialogService>();
        var (viewModel, _) = BuildWithSharedSources([source], dialogs, out _, local);
        await viewModel.LoadAsync();

        await viewModel.ToggleSharingAsync(local);

        await dialogs.DidNotReceive().ShowShareProjectDialogAsync(Arg.Any<Project>(), Arg.Any<IReadOnlyList<ISharedProjectSource>>());
    }

    // AC-744: a project's Memory reference can start with a registered source's prefix without ever having gone
    // through the publish flow (a manually-typed `depot:cockpit`-style reference, say) — that alone must not read
    // as "already shared", either for the toggle's label or for which flow clicking it opens.
    [Fact]
    public async Task ToggleSharingAsync_PrefixMatchingButNeverPublishedMemoryReference_ReadsAndActsAsNotYetShared()
    {
        var local = Project.Create("Cockpit") with
        {
            Resources = [new ProjectResource("Depot — Work:cockpit", ProjectResourceRole.Memory)],
        };
        // The source never lists "Depot — Work:cockpit" — nothing claims its ownership, unlike the genuinely
        // shared case in ToggleSharingAsync_AlreadySharedProject_ConfirmsThenRemovesOnlyTheBindingRow below.
        var source = new _FakeSharedProjectSource(
            "Depot — Work", SharedProjectListResult.Success([]), publishResult: SharedProjectPublishResult.Success("depot:new-project"));
        var dialogs = Substitute.For<ISessionDialogService>();
        var (viewModel, _) = BuildWithSharedSources([source], dialogs, out _, local);
        await viewModel.LoadAsync();
        await viewModel.SharedProjectsLoadTask;
        viewModel.SelectedProject = local;

        Assert.Equal("Share…", viewModel.ShareToggleLabel);

        await viewModel.ToggleSharingAsync(local);

        await dialogs.Received(1).ShowShareProjectDialogAsync(local, Arg.Any<IReadOnlyList<ISharedProjectSource>>());
        await dialogs.DidNotReceive().ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ToggleSharingAsync_AlreadySharedProject_ConfirmsThenRemovesOnlyTheBindingRow()
    {
        var extraMemoryRow = new ProjectResource("~/Notes/payroll.md", ProjectResourceRole.Memory) { Label = "Personal notes" };
        var bound = Project.Create("PayrollProcessor") with
        {
            Resources = [new ProjectResource("Depot — Work:payroll-processor", ProjectResourceRole.Memory), extraMemoryRow],
        };
        var source = new _FakeSharedProjectSource("Depot — Work", SharedProjectListResult.Success([new SharedProject("Depot — Work:payroll-processor", "PayrollProcessor")]));
        var dialogs = Substitute.For<ISessionDialogService>();
        dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var (viewModel, _) = BuildWithSharedSources([source], dialogs, out var store, bound);
        await viewModel.LoadAsync();
        await viewModel.SharedProjectsLoadTask; // claims the ownership ToggleSharingAsync's _ResolveSharedSource reads
        viewModel.SelectedProject = bound;

        Assert.Equal("Stop sharing…", viewModel.ShareToggleLabel);

        await viewModel.ToggleSharingAsync(bound);

        await store.Received(1).SaveAsync(Arg.Is<ProjectSettings>(settings =>
            !settings.Projects.Single().Resources.Any(resource => resource.Reference == "Depot — Work:payroll-processor")
            && settings.Projects.Single().Resources.Contains(extraMemoryRow)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ToggleSharingAsync_AlreadySharedProject_ConfirmationDeclined_LeavesTheBindingIntact()
    {
        var bound = Project.Create("PayrollProcessor") with
        {
            Resources = [new ProjectResource("Depot — Work:payroll-processor", ProjectResourceRole.Memory)],
        };
        var source = new _FakeSharedProjectSource("Depot — Work", SharedProjectListResult.Success([new SharedProject("Depot — Work:payroll-processor", "PayrollProcessor")]));
        var dialogs = Substitute.For<ISessionDialogService>();
        dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        var (viewModel, _) = BuildWithSharedSources([source], dialogs, out var store, bound);
        await viewModel.LoadAsync();
        await viewModel.SharedProjectsLoadTask;
        store.ClearReceivedCalls();

        await viewModel.ToggleSharingAsync(bound);

        await store.DidNotReceive().SaveAsync(Arg.Any<ProjectSettings>(), Arg.Any<CancellationToken>());
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
        private readonly SharedProjectPublishResult? _publishResult;

        public _FakeSharedProjectSource(
            string sourceName, SharedProjectListResult? result = null, Exception? exception = null, bool neverCompletes = false,
            SharedProjectPublishResult? publishResult = null)
        {
            SourceName = sourceName;
            _result = result;
            _exception = exception;
            _neverCompletes = neverCompletes;
            _publishResult = publishResult;
        }

        public SharedProjectPublishDefinition? LastPublishedDefinition { get; private set; }

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

        // AC-247: not exercised by any test in this file either (none of them call ProjectDialogViewModel.SaveAsync) — same reasoning as PrepareBindingAsync above.
        public Task<SharedProjectWriteBackResult> WriteBackAsync(string id, SharedProjectDefinitionEdit edit, string baseChecksum, CancellationToken cancellationToken) =>
            Task.FromResult(SharedProjectWriteBackResult.Failed("not implemented by this fake"));

        // AC-620: CanPublish tracks whether this fake was given a publishResult to answer with, so a test that
        // never asks for publish support does not have to opt out of it separately.
        public bool CanPublish => _publishResult is not null;

        public Task<SharedProjectPublishTargetListResult> ListPublishTargetsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SharedProjectPublishTargetListResult.Success([new SharedProjectPublishTarget($"{Key}:target", "target", "Owner")]));

        public Task<SharedProjectPublishResult> PublishAsync(string targetId, SharedProjectPublishDefinition definition, CancellationToken cancellationToken)
        {
            LastPublishedDefinition = definition;
            return Task.FromResult(_publishResult ?? SharedProjectPublishResult.Failed("not implemented by this fake"));
        }
    }
}
