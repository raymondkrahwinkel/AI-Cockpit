using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Workspaces;

namespace Cockpit.Infrastructure.Tests.Configuration;

// AC-1750: a keyed workspace keeps its key, its body-set title and its preview state across a restart.
public sealed class WorkspaceSettingsStoreInstanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cockpit-workspace-instances-{Guid.NewGuid():N}");

    public WorkspaceSettingsStoreInstanceTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    // Layer 2, persistence.
    [Fact]
    public async Task AKeyedWorkspace_KeepsItsKeyTitleAndPreviewState_ThroughSaveAndLoad()
    {
        var store = new WorkspaceSettingsStore(Path.Combine(_directory, "cockpit.json"));
        var type = new WorkspaceType("depot.markdown");
        var kept = Workspace.Create("Notes.md", type) with { InstanceKey = "depot:c1:org/p:Notes.md" };
        var preview = Workspace.Create("depot-sync.md", type) with { InstanceKey = "depot:c1:org/p:Ideas/depot-sync.md", IsPreview = true };

        await store.SaveAsync(WorkspaceSettings.Default.WithWorkspace(kept).WithWorkspace(preview));
        var loaded = (await store.LoadAsync()).Workspaces;

        Assert.Equal(("Notes.md", kept.InstanceKey, false), _Shape(loaded.Single(workspace => workspace.Id == kept.Id)));
        Assert.Equal(("depot-sync.md", preview.InstanceKey, true), _Shape(loaded.Single(workspace => workspace.Id == preview.Id)));
        Assert.All(loaded.Where(workspace => workspace.Type != type), workspace => Assert.Equal((null, false), (workspace.InstanceKey, workspace.IsPreview)));
    }

    private static (string, string?, bool) _Shape(Workspace workspace) => (workspace.Name, workspace.InstanceKey, workspace.IsPreview);
}
