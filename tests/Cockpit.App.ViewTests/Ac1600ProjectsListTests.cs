using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Projects;
using Cockpit.Plugins.Abstractions.Projects;
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
                }
                finally
                {
                    window.Close();
                }
            });
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
    }
}
