using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.App.ViewTests;

// AC-1750: a plugin workspace opened per key, and the preview tab the next preview replaces in place (mockup AC-1749 v003).
[Collection("avalonia")]
public sealed class WorkspaceInstanceTests
{
    private const string TypeId = "test.markdown";

    private readonly Dictionary<string, IWorkspaceContext> _contexts = new(StringComparer.Ordinal);

    private WorkspacesViewModel _Workspaces()
    {
        var registry = new WorkspaceTypeRegistry(new ServiceCollection().BuildServiceProvider());
        registry.Register(
            new WorkspaceTypeRegistration(TypeId, "Markdown", context =>
            {
                _contexts[context.InstanceKey!] = context;
                return new TextBlock { Text = context.InstanceKey };
            }),
            new NoStorage(),
            NullCockpitSessionObserver.Instance);
        return new WorkspacesViewModel(store: null, workspaceTypes: registry);
    }

    private static List<string?> _KeyedTabs(WorkspacesViewModel workspaces) =>
        [.. workspaces.Settings.Workspaces.Where(workspace => workspace.Type.Id == TypeId).Select(workspace => workspace.InstanceKey)];

    // Layer 2, view.
    [Fact]
    public Task OpeningTheSameKeyAgain_FocusesItsTab_WhileAnotherKeyOfTheSameTypeGetsATabOfItsOwn() => HeadlessAvalonia.RunAsync(async () =>
    {
        var workspaces = _Workspaces();

        await workspaces.OpenWorkspaceAsync(TypeId, new WorkspaceOpenOptions("a.md", "a.md"));
        await workspaces.SelectWorkspaceCommand.ExecuteAsync(workspaces.Settings.Workspaces[0].Id);
        await workspaces.OpenWorkspaceAsync(TypeId, new WorkspaceOpenOptions("a.md", "a.md"));

        Assert.Equal(["a.md"], _KeyedTabs(workspaces));
        Assert.Equal("a.md", workspaces.Active?.InstanceKey);
        Assert.Equal("a.md", workspaces.Active?.Name);

        await workspaces.OpenWorkspaceAsync(TypeId, new WorkspaceOpenOptions("b.md", "b.md"));

        Assert.Equal(["a.md", "b.md"], _KeyedTabs(workspaces));
        Assert.Equal("b.md", workspaces.Active?.InstanceKey);
    });

    // Layer 2, view.
    [Fact]
    public Task ASecondPreview_ReplacesTheFirstInItsSlot_AndADoubleClickPinsIt_SoTheNextPreviewOpensBesideIt() => HeadlessAvalonia.RunAsync(async () =>
    {
        var workspaces = _Workspaces();
        await workspaces.OpenWorkspaceAsync(TypeId, new WorkspaceOpenOptions("kept.md", "kept.md"));
        await workspaces.OpenWorkspaceAsync(TypeId, new WorkspaceOpenOptions("one.md", "one.md", Preview: true));
        var slot = workspaces.Tabs.ToList().FindIndex(tab => tab.IsActive);
        var firstClosed = false;
        _contexts["one.md"].Closed += (_, _) => firstClosed = true;

        await workspaces.OpenWorkspaceAsync(TypeId, new WorkspaceOpenOptions("two.md", "two.md", Preview: true));

        Assert.Equal(["kept.md", "two.md"], _KeyedTabs(workspaces));
        Assert.True(firstClosed);
        Assert.Equal(slot, workspaces.Tabs.ToList().FindIndex(tab => tab.IsActive));
        Assert.Equal("two.md", (workspaces.ActivePluginBody as TextBlock)?.Text);
        Assert.True(_contexts["two.md"].IsPreview);

        var pinned = false;
        _contexts["two.md"].Pinned += (_, _) => pinned = true;
        await workspaces.DoubleClickTabAsync(workspaces.Tabs[slot]);

        Assert.True(pinned);
        Assert.False(workspaces.Tabs[slot].IsPreview || workspaces.Tabs[slot].IsRenaming);

        await workspaces.OpenWorkspaceAsync(TypeId, new WorkspaceOpenOptions("three.md", "three.md", Preview: true));

        Assert.Equal(["kept.md", "two.md", "three.md"], _KeyedTabs(workspaces));
        Assert.Single(workspaces.Settings.Workspaces, workspace => workspace.IsPreview);
    });

    private sealed class NoStorage : IPluginStorage
    {
        public T? Get<T>(string key) => default;

        public void Set<T>(string key, T value)
        {
        }
    }
}
