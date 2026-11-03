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
