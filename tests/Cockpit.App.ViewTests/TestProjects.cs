using Avalonia.Threading;
using NSubstitute;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Projects;

namespace Cockpit.App.ViewTests;

// AC-1435: the Projects page over the catalog production registers, so a test's writes go where the assistant's do.
internal static class TestProjects
{
    public static ProjectCatalog Catalog(
        IProjectStore? store = null,
        ISharedProjectSourceRegistry? sharedSources = null,
        IProjectOwnershipRegistry? ownership = null)
    {
        if (store is null)
        {
            store = Substitute.For<IProjectStore>();
            store.LoadAsync(Arg.Any<CancellationToken>()).Returns(ProjectSettings.Empty);
        }

        return new ProjectCatalog(store, ownership ?? new ProjectOwnershipRegistry(), sharedSources ?? new SharedProjectSourceRegistry());
    }

    public static ProjectsViewModel ViewModel(
        IProjectStore store,
        ISessionDialogService? dialogs,
        ISharedProjectSourceRegistry? sharedSources = null) =>
        ViewModel(Catalog(store, sharedSources), dialogs);

    public static ProjectsViewModel ViewModel(ProjectCatalog catalog, ISessionDialogService? dialogs) => new(catalog, catalog, dialogs);

    // The page once the UI thread drew every change the catalog announced: Changed posts its redraw there, and a job
    // queued at Background runs after it.
    public static ProjectsViewModel Drawn(this ProjectsViewModel projects)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.RunJobs();
        }
        else
        {
            Dispatcher.UIThread.Invoke(() => { }, DispatcherPriority.Background);
        }

        return projects;
    }
}
