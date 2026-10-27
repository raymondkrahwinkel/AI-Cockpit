using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Projects;
using Cockpit.Plugins.Abstractions.Projects;
using CommunityToolkit.Mvvm.Input;
using NSubstitute;

namespace Cockpit.App.ViewTests;

// AC-1600: the B2 Projects list, measured on the real markup.
[Collection("avalonia")]
public sealed class Ac1600ProjectsListTests
{
    // Layer 2 (view). The columns follow the list's width, not the window's: at a 1100 px window the window is past
    // the 1050 px threshold but the list beside the detail pane is not, so the description must sit under the name;
    // at 1920 the list itself is wide and the description takes its own column.
    [Fact]
    public void TheDescriptionColumn_FollowsTheListWidth_NotTheWindow()
    {
        foreach (var (width, height, wide) in new[] { (1100, 760, false), (1920, 1080, true) })
        {
            HeadlessAvalonia.Run(() =>
            {
                var window = Screenshotter.ShowScene("projects-workspace", width, height);
                try
                {
                    window.UpdateLayout();
                    var list = window.GetVisualDescendants().OfType<DockPanel>().Single(panel => panel.Name == "ProjectList");
                    Assert.Equal(wide, list.Bounds.Width >= ProjectListGrid.WideListWidth);

                    var row = window.GetVisualDescendants().OfType<ProjectListRow>().First(row => row.IsEffectivelyVisible);
                    var column = row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "DescriptionColumn");
                    var underName = row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "DescriptionUnderName");

                    Assert.True(wide == column.IsEffectivelyVisible, $"at {width} px the description column shows: {column.IsEffectivelyVisible}");
                    Assert.True(wide != underName.IsEffectivelyVisible, $"at {width} px the description under the name shows: {underName.IsEffectivelyVisible}");

                    ((CockpitViewModel)window.DataContext!).Projects.Density = ProjectsDensity.Compact;
                    window.UpdateLayout();
                    var descriptionHeader = window.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "Description");
                    Assert.True(row.IsCompact);
                    Assert.False(column.IsEffectivelyVisible);
                    Assert.False(underName.IsEffectivelyVisible);
                    Assert.False(descriptionHeader.IsEffectivelyVisible);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        _AssertRefreshStates();
    }

    private static void _AssertRefreshStates() => HeadlessAvalonia.Run(() =>
    {
        AssertScene("projects-workspace-loading", "Refreshing…", "Loading from Depot…", expectsTryAgain: false);
        AssertScene("projects-workspace", "Refresh", sectionText: null, expectsTryAgain: false);
        AssertScene("projects-workspace-stale", "Refresh", "Couldn't reach Depot — showing the last known list · just now", expectsTryAgain: true);
    });

    private static void AssertScene(string scene, string toolbarText, string? sectionText, bool expectsTryAgain)
    {
        var window = Screenshotter.ShowScene(scene, 1100, 760);
        try
        {
            window.UpdateLayout();
            var text = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(candidate => candidate.IsEffectivelyVisible)
                .Select(candidate => candidate.Text)
                .ToList();

            Assert.Contains(toolbarText, text);
            if (sectionText is not null)
            {
                Assert.Contains(sectionText, text);
            }
            Assert.Equal(expectsTryAgain, text.Contains("Try again"));
            if (scene == "projects-workspace-loading")
            {
                Assert.Equal(3, text.Count(candidate => candidate == "Loading from Depot…"));
            }
            else
            {
                Assert.DoesNotContain("Loading from Depot…", text);
            }
        }
        finally
        {
            window.Close();
        }
    }

    // Layer 2 (view). Entering selects the first row; a pick then stays on its project when the catalog hands over a
    // new list (Refresh, or an event), even though every row is rebuilt.
    [Fact]
    public async Task TheSelection_StaysOnItsProject_WhenTheCatalogRefreshes()
    {
        var source = Substitute.For<ISharedProjectSource>();
        source.Key.Returns("depot");
        source.SourceName.Returns("Depot — Work");
        source.ListAsync(Arg.Any<CancellationToken>()).Returns(
            SharedProjectListResult.Success([new SharedProject("depot:alpha", "Alpha"), new SharedProject("depot:beta", "Beta")]),
            SharedProjectListResult.Success(
                [new SharedProject("depot:aardvark", "Aardvark"), new SharedProject("depot:alpha", "Alpha"), new SharedProject("depot:beta", "Beta")]));
        var registry = Substitute.For<ISharedProjectSourceRegistry>();
        registry.Sources.Returns([source]);
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(ProjectSettings.Empty);

        var projects = TestProjects.ViewModel(store, Substitute.For<ISessionDialogService>(), registry);
        await projects.LoadAsync();
        await projects.SharedProjectsLoadTask;
        Assert.Equal("depot:alpha", projects.SelectedListItem?.Id);

        var beta = projects.ProjectSections.SelectMany(section => section.Items).Single(row => row.Id == "depot:beta");
        projects.SelectListItemCommand.Execute(beta);

        await projects.LoadSharedProjectsAsync();

        var rows = projects.ProjectSections.SelectMany(section => section.Items).ToList();
        Assert.Equal("depot:aardvark", rows[0].Id);
        Assert.NotSame(beta, projects.SelectedListItem);
        Assert.Equal("depot:beta", projects.SelectedListItem?.Id);
        Assert.Equal(["depot:beta"], rows.Where(row => row.IsSelected).Select(row => row.Id));

        var starts = 0;
        var keyboardStore = Substitute.For<IProjectStore>();
        keyboardStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(ProjectSettings.Empty
            .WithProject(Project.Create("Alpha") with { DefaultProfileLabel = "personal" })
            .WithProject(Project.Create("Beta") with { DefaultProfileLabel = "personal" }));
        var keyboardProjects = TestProjects.ViewModel(keyboardStore, Substitute.For<ISessionDialogService>());
        keyboardProjects.CardActions = new ProjectCardActions(
            new RelayCommand<Project>(_ => starts++),
            new RelayCommand<Project>(_ => { }),
            new RelayCommand<ProjectJobChoice>(_ => { }),
            new RelayCommand<Project>(_ => { }),
            new RelayCommand<Project>(_ => { }),
            new RelayCommand<Project>(_ => { }),
            new RelayCommand<Project>(_ => { }));
        await keyboardProjects.LoadAsync();

        HeadlessAvalonia.Run(() =>
        {
            var workspace = new ProjectsWorkspace { DataContext = keyboardProjects };
            var window = new Window { Content = workspace, Width = 800, Height = 600 };
            window.Show();
            try
            {
                window.UpdateLayout();
                var list = workspace.GetVisualDescendants().OfType<DockPanel>().Single(panel => panel.Name == "ProjectList");
                list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
                var selected = keyboardProjects.SelectedListItem;
                Assert.Equal("Beta", selected?.Name);
                list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                Assert.Equal(1, starts);

                var moreButton = list.GetVisualDescendants().OfType<Button>()
                    .First(button => ToolTip.GetTip(button) is "More for this project");
                moreButton.Focus();
                moreButton.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                Assert.Equal(1, starts);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
