using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Docking;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Layout;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Layout;
using Cockpit.Infrastructure.Workspaces;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.App.ViewTests;

// AC-1751: a workspace popped out into its own window and docked back, as the same body (mockup AC-1749 v003, flow 8).
[Collection("avalonia")]
public sealed class WorkspacePopOutTests
{
    private const string TypeId = "test.markdown";

    private readonly Dictionary<string, IWorkspaceContext> _contexts = new(StringComparer.Ordinal);

    private WorkspacesViewModel _Workspaces(IWorkspaceSettingsStore? store = null)
    {
        var registry = new WorkspaceTypeRegistry(new ServiceCollection().BuildServiceProvider());
        registry.Register(
            new WorkspaceTypeRegistration(TypeId, "Markdown", context =>
            {
                _contexts[context.InstanceKey!] = context;
                return new TextBlock { Text = context.InstanceKey };
            }) { CanFloat = true },
            new NoStorage(),
            NullCockpitSessionObserver.Instance);
        registry.Register(new WorkspaceTypeRegistration("test.docked", "Docked", _ => new TextBlock()), new NoStorage(), NullCockpitSessionObserver.Instance);
        return new WorkspacesViewModel(store, workspaceTypes: registry);
    }

    // Layer 2, view.
    [Fact]
    public Task PopOutAndDockBack_MoveTheSameBody_PinAPreview_AndTheTabLeavesAndComesBackToItsPlace() => HeadlessAvalonia.RunAsync(async () =>
    {
        var workspaces = _Workspaces();
        var host = new WorkspaceWindowHost(workspaces, _ => Task.CompletedTask, () => null);
        host.Start();
        await workspaces.OpenWorkspaceAsync("test.docked");
        Assert.False(workspaces.CanPopOutActive);

        await workspaces.OpenWorkspaceAsync(TypeId, new WorkspaceOpenOptions("a.md", "a.md", Preview: true));
        var popped = workspaces.Active!;
        var slot = workspaces.Tabs.ToList().FindIndex(tab => tab.Id == popped.Id);
        var body = workspaces.ActivePluginBody;
        var pinned = false;
        _contexts["a.md"].Pinned += (_, _) => pinned = true;
        Assert.True(workspaces.CanPopOutActive);

        await workspaces.PopOutAsync(popped.Id);
        Dispatcher.UIThread.RunJobs();

        var window = host.WindowFor(popped.Id);
        Assert.NotNull(window);
        Assert.Contains(body!, window.GetVisualDescendants());
        Assert.True(pinned);
        Assert.DoesNotContain(workspaces.Tabs, tab => tab.Id == popped.Id);
        Assert.NotEqual(popped.Id, workspaces.Active?.Id);

        await workspaces.OpenWorkspaceAsync(TypeId, new WorkspaceOpenOptions("a.md", "a.md"));
        Assert.Single(workspaces.Settings.Workspaces, workspace => workspace.InstanceKey == "a.md");
        Assert.DoesNotContain(workspaces.Tabs, tab => tab.Id == popped.Id);

        await host.DockBackAsync(popped.Id);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(host.WindowFor(popped.Id));
        Assert.Equal(slot, workspaces.Tabs.ToList().FindIndex(tab => tab.Id == popped.Id && tab.IsActive));
        Assert.Same(body, workspaces.ActivePluginBody);
        Assert.False(workspaces.Active!.IsPreview);
    });

    // Layer 2, view.
    [Fact]
    public Task AFloatingWorkspace_StaysFloatingWhenTheCockpitCloses_AndReopensInItsWindowAtItsBounds() => HeadlessAvalonia.RunAsync(async () =>
    {
        var config = Path.Combine(Path.GetTempPath(), $"cockpit-ac1751-{Guid.NewGuid():n}.json");
        try
        {
            var store = new WorkspaceSettingsStore(config);
            var bounds = new WindowBoundsStore(config);
            var workspaces = _Workspaces(store);
            var host = new WorkspaceWindowHost(workspaces, _ => Task.CompletedTask, () => bounds);
            host.Start();
            await workspaces.OpenWorkspaceAsync(TypeId, new WorkspaceOpenOptions("a.md", "a.md"));
            var id = workspaces.Active!.Id;
            await bounds.SaveAsync(id, new WindowBounds(120, 80, 700, 500, IsMaximized: false));
            await workspaces.PopOutAsync(id);
            Assert.Equal(new PixelPoint(120, 80), host.WindowFor(id)!.Position);

            var saved = new TaskCompletionSource();
            void OnWritten(string path) => _ = path == config && saved.TrySetResult();
            CockpitConfigFileAccess.Written += OnWritten;
            host.CloseWithTheCockpit();
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            CockpitConfigFileAccess.Written -= OnWritten;

            var restarted = _Workspaces(store);
            await restarted.InitializeAsync();
            var restartedHost = new WorkspaceWindowHost(restarted, _ => Task.CompletedTask, () => bounds);
            restartedHost.Start();

            Assert.DoesNotContain(restarted.Tabs, tab => tab.Id == id);
            var reopened = restartedHost.WindowFor(id);
            Assert.NotNull(reopened);
            Assert.Equal(new PixelPoint(120, 80), reopened.Position);
            Assert.Equal((700d, 500d), (reopened.Width, reopened.Height));
            restartedHost.CloseWithTheCockpit();
        }
        finally
        {
            File.Delete(config);
        }
    });

    private sealed class NoStorage : IPluginStorage
    {
        public T? Get<T>(string key) => default;

        public void Set<T>(string key, T value)
        {
        }
    }
}
