using NSubstitute;
using Cockpit.Infrastructure.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.ViewModels;

// AC-161: the manager owns the persisting the editor deliberately does not, so what it writes is the feature's source of truth.
public class ProjectsViewModelTests
{
    private static (ProjectsViewModel ViewModel, IProjectStore Store, ISessionDialogService Dialogs) Build(
        params Project[] saved)
    {
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings { Projects = saved });

        var dialogs = Substitute.For<ISessionDialogService>();
        var catalog = new ProjectCatalog(store, new ProjectOwnershipRegistry(), new _FakeSharedProjectSourceRegistry([]));
        return (new ProjectsViewModel(catalog, catalog, dialogs), store, dialogs);
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

    // AC-245: the shared-project catalog — grouping, filtering an already-bound or hidden project out, one
    // source's failure not costing the others, and the AC-604 ownership claim this consumes it through.

    private static (ProjectsViewModel ViewModel, IProjectOwnershipRegistry Ownership) BuildWithSharedSources(
        IReadOnlyList<ISharedProjectSource> sources, ISessionDialogService? dialogs, out IProjectStore store, params Project[] saved)
    {
        store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings { Projects = saved });

        var ownership = new ProjectOwnershipRegistry();
        var registry = new _FakeSharedProjectSourceRegistry(sources);
        var catalog = new ProjectCatalog(store, ownership, registry);
        var viewModel = new ProjectsViewModel(catalog, catalog, dialogs);
        return (viewModel, ownership);
    }

    // AC-246: the "Finish setting up…" bind step. FinishSettingUpAsync itself never talks to Depot — it only finds
    // the right ISharedProjectSource by SharedProject.Id's own scheme prefix and hands off to ISessionDialogService,
    // so these tests fake the dialog rather than the plugin read (that is DepotSharedProjectSourcePrepareBindingTests'
    // and SharedProjectBindingDialogViewModelTests' job).

    // AC-618: the category groups the list actually renders, and the per-card origin badge that replaces AC-245's
    // separate "On this machine" heading.

    // AC-762: the ownership registry is purely in-memory and rebuilt from a slow, unretried network call, so a
    // project that was genuinely published must not render as local for however long that call has not finished
    // (or has failed) — Project.SharedSourceName is the persisted "last known truth" fallback for exactly that gap.

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

    private sealed class _FakeSharedProjectSourceRegistry(IReadOnlyList<ISharedProjectSource> initialSources) : ISharedProjectSourceRegistry
    {
        private readonly List<ISharedProjectSource> _sources = [.. initialSources];

        public IReadOnlyList<ISharedProjectSource> Sources => _sources;

        public event Action<ISharedProjectSource>? Registered;

        public bool Register(ISharedProjectSource source)
        {
            _sources.Add(source);
            Registered?.Invoke(source);
            return true;
        }

        public void Remove(string key) => _sources.RemoveAll(source => source.Key == key);
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
