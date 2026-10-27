using Avalonia.Controls;
using Avalonia.Threading;
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

[Collection("avalonia")]
public sealed class HideSharedProjectsTests
{
    // Layer 2 (view): the row menu hides locally, the next row is selected, a reload keeps it hidden, and the section
    // link restores every hidden project for its connection and organisation.
    [Fact]
    public async Task TheRowMenu_HidesLocallyAcrossReload_AndTheSectionLinkRestores()
    {
        var settings = ProjectSettings.Empty;
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(settings));
        store.SaveAsync(Arg.Any<ProjectSettings>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            settings = call.Arg<ProjectSettings>();
            return Task.CompletedTask;
        });
        var source = Substitute.For<ISharedProjectSource>();
        source.Key.Returns("depot");
        source.SourceName.Returns("Depot — Work");
        source.ListAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(SharedProjectListResult.Success([
            new SharedProject("depot:alpha", "Alpha") { Organization = new SharedProjectOrganization("acme", "Acme", "Editor") },
            new SharedProject("depot:beta", "Beta") { Organization = new SharedProjectOrganization("acme", "Acme", "Editor") },
        ])));
        var registry = new SharedProjectSourceRegistry();
        registry.Register(source);

        var projects = TestProjects.ViewModel(store, Substitute.For<ISessionDialogService>(), registry);
        await projects.LoadAsync();
        await projects.SharedProjectsLoadTask;

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var workspace = new ProjectsWorkspace { DataContext = projects };
            var window = new Window { Content = workspace, Width = 1100, Height = 800 };
            window.Show();
            try
            {
                window.UpdateLayout();
                var alphaRow = workspace.GetVisualDescendants().OfType<ProjectListRow>()
                    .Single(row => ((ProjectListItemViewModel)row.DataContext!).Id == "depot:alpha");
                var more = alphaRow.GetVisualDescendants().OfType<Button>()
                    .Single(button => ToolTip.GetTip(button) is "More for this project");
                var flyout = Assert.IsType<Flyout>(more.Flyout);
                flyout.ShowAt(more);
                Dispatcher.UIThread.RunJobs();
                var hide = ((Panel)flyout.Content!).Children.OfType<Button>()
                    .Single(button => Equals(button.Content, "Hide on this machine"));

                var hideCommand = Assert.IsAssignableFrom<IAsyncRelayCommand>(hide.Command);
                hideCommand.Execute(hide.CommandParameter);
                await hideCommand.ExecutionTask!;
                window.UpdateLayout();

                Assert.Equal(["depot:alpha"], settings.HiddenSharedProjectIds);
                Assert.Equal("depot:beta", projects.SelectedListItem?.Id);
                Assert.DoesNotContain(projects.ProjectSections.SelectMany(section => section.Items), item => item.Id == "depot:alpha");
            }
            finally
            {
                window.Close();
            }
        });

        var reloaded = TestProjects.ViewModel(store, Substitute.For<ISessionDialogService>(), registry);
        await reloaded.LoadAsync();
        await reloaded.SharedProjectsLoadTask;
        var section = Assert.Single(reloaded.ProjectSections, candidate => candidate.OrganizationSlug == "acme");
        Assert.Equal(1, section.HiddenCount);
        Assert.DoesNotContain(section.Items, item => item.Id == "depot:alpha");

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var workspace = new ProjectsWorkspace { DataContext = reloaded };
            var window = new Window { Content = workspace, Width = 1100, Height = 800 };
            window.Show();
            try
            {
                window.UpdateLayout();
                var header = workspace.GetVisualDescendants().OfType<ProjectSectionHeader>()
                    .Single(control => ((ProjectSectionViewModel)control.DataContext!).OrganizationSlug == "acme");
                var restore = header.GetVisualDescendants().OfType<Button>()
                    .Single(button => Equals(button.Content, "· 1 hidden"));

                var restoreCommand = Assert.IsAssignableFrom<IAsyncRelayCommand>(restore.Command);
                restoreCommand.Execute(restore.CommandParameter);
                await restoreCommand.ExecutionTask!;

                Assert.Empty(settings.HiddenSharedProjectIds);
                Assert.Contains(reloaded.ProjectSections.SelectMany(candidate => candidate.Items), item => item.Id == "depot:alpha");
            }
            finally
            {
                window.Close();
            }
        });
    }
}
