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

    private WorkspacesViewModel _Workspaces() => new(store: null, workspaceTypes: _Registry());

    private WorkspaceTypeRegistry _Registry()
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
        return registry;
    }

    // Strip: Sessions (3 sample sessions) · Projects · Empty · dirty.md (unsaved) · preview.md (preview).
    private async Task<CockpitViewModel> _BulkStripAsync()
    {
        var cockpit = new CockpitViewModel(workspaceTypeRegistry: _Registry());
        foreach (var session in cockpit.Sessions)
        {
            session.WorkspaceId = _IdOf(cockpit, "Sessions");
        }
        await cockpit.Workspaces.CreateSessionsWorkspaceAsync("Empty");
        await cockpit.Workspaces.OpenWorkspaceAsync(TypeId, new WorkspaceOpenOptions("dirty.md", "dirty.md"));
        _contexts["dirty.md"].SetMarker(WorkspaceMarker.Unsaved);
        await cockpit.Workspaces.OpenWorkspaceAsync(TypeId, new WorkspaceOpenOptions("preview.md", "preview.md", Preview: true));
        return cockpit;
    }

    private static string _IdOf(CockpitViewModel cockpit, string name) =>
        cockpit.Workspaces.Settings.Workspaces.Single(workspace => workspace.Name == name).Id;

    // AC-1972, layer 2, data loss: each bulk close takes its own set, never Projects, and counts neither the preview
    // nor the empty desk.
    [Theory]
    [InlineData(BulkClose.Others, "Empty", "Sessions,dirty.md,preview.md", 2)]
    [InlineData(BulkClose.ToTheRight, "Sessions", "Empty,dirty.md,preview.md", 1)]
    [InlineData(BulkClose.All, "Empty", "Sessions,Empty,dirty.md,preview.md", 2)]
    public Task BulkClose_TakesItsOwnSet_SkipsWhatCannotClose_AndCountsOnlyTabsWithSomethingToLose(
        BulkClose action, string clicked, string expectedClosing, int expectedCount) => HeadlessAvalonia.RunAsync(async () =>
    {
        var cockpit = await _BulkStripAsync();

        var plan = cockpit.PlanBulkClose(action, _IdOf(cockpit, clicked));

        Assert.Equal(expectedClosing.Split(','), plan.WorkspaceIds.Select(id => cockpit.Workspaces.Settings.Workspaces.Single(workspace => workspace.Id == id).Name));
        Assert.Equal(expectedCount, plan.Count);
        Assert.Equal($"Close {expectedCount} workspace{(expectedCount == 1 ? "" : "s")}?", plan.Title);
        Assert.Contains("dirty.md has unsaved changes. You will be asked about it before it closes.", plan.Message);
        Assert.Contains("Projects stays open. It cannot be closed.", plan.Message);
        Assert.DoesNotContain("preview.md", plan.Message);
    });

    private static List<string?> _KeyedTabs(WorkspacesViewModel workspaces) =>
        [.. workspaces.Settings.Workspaces.Where(workspace => workspace.Type.Id == TypeId).Select(workspace => workspace.InstanceKey)];

    private async Task _AssertSameKeyFocusAsync()
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
    }

    // Layer 2, view.
    [Fact]
    public Task ASecondPreview_ReplacesTheFirstInItsSlot_AndADoubleClickPinsIt_SoTheNextPreviewOpensBesideIt() => HeadlessAvalonia.RunAsync(async () =>
    {
        await _AssertSameKeyFocusAsync();
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
        var guarded = workspaces.Active ?? throw new InvalidOperationException("No active preview workspace.");
        _contexts["three.md"].SetCloseGuard(() => Task.FromResult(false));
        await workspaces.CloseWorkspaceCommand.ExecuteAsync(guarded.Id);
        Assert.Contains(workspaces.Settings.Workspaces, workspace => workspace.Id == guarded.Id);
        _contexts["three.md"].SetCloseGuard(() => Task.FromResult(true));
        await workspaces.CloseWorkspaceCommand.ExecuteAsync(guarded.Id);
        Assert.DoesNotContain(workspaces.Settings.Workspaces, workspace => workspace.Id == guarded.Id);

        // AC-1972: Cancel on the bulk question (no dialog answers no) closes nothing; after it, every guard runs and
        // a refusing one keeps only its own tab.
        var cockpit = await _BulkStripAsync();
        var before = cockpit.Workspaces.Settings.Workspaces.Count;
        await cockpit.CloseWorkspacesWithConfirmationAsync(BulkClose.All, _IdOf(cockpit, "Empty"));
        Assert.Equal(before, cockpit.Workspaces.Settings.Workspaces.Count);
        _contexts["dirty.md"].SetCloseGuard(() => Task.FromResult(false));
        var previewAsked = false;
        _contexts["preview.md"].SetCloseGuard(() => Task.FromResult(previewAsked = true));
        await cockpit.CloseWorkspacesAsync(cockpit.PlanBulkClose(BulkClose.All, _IdOf(cockpit, "Empty")).WorkspaceIds);
        Assert.True(previewAsked);
        Assert.Equal(["Projects", "dirty.md"], cockpit.Workspaces.Settings.Workspaces.Select(workspace => workspace.Name));
    });

    private sealed class NoStorage : IPluginStorage
    {
        public T? Get<T>(string key) => default;

        public void Set<T>(string key, T value)
        {
        }
    }
}
