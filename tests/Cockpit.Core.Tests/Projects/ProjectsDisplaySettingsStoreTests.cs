using Cockpit.Core.Layout;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Layout;
using Cockpit.Infrastructure.Projects;

namespace Cockpit.Core.Tests.Projects;

/// <summary>
/// AC-772: the Projects page's layout choice in the <c>projectsDisplay</c> section of <c>cockpit.json</c>. Same
/// shape as the layout store, plus its own rule — a stored <c>Continue</c> falls back while that layout is hidden.
/// </summary>
public class ProjectsDisplaySettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public ProjectsDisplaySettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task LoadAsync_NoConfigFile_ReturnsCards()
    {
        var store = new ProjectsDisplaySettingsStore(_configFilePath);

        Assert.Equal(ProjectsLayoutMode.Cards, (await store.LoadAsync()).LayoutMode);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsTheLayout()
    {
        var store = new ProjectsDisplaySettingsStore(_configFilePath);

        await store.SaveAsync(new ProjectsDisplaySettings { LayoutMode = ProjectsLayoutMode.List });

        Assert.Equal(ProjectsLayoutMode.List, (await store.LoadAsync()).LayoutMode);
    }

    [Fact]
    public async Task LoadAsync_AStoredContinue_FallsBackWhileThatLayoutIsNotOffered()
    {
        // Written straight to disk rather than through SaveAsync, which normalizes on the way in too — the point here
        // is the read side, which is what protects against a config this build did not write.
        await File.WriteAllTextAsync(_configFilePath, """{ "ProjectsDisplay": { "LayoutMode": "Continue" } }""");
        var store = new ProjectsDisplaySettingsStore(_configFilePath);

        var expected = ProjectsDisplaySettings.ContinueLayoutAvailable
            ? ProjectsLayoutMode.Continue
            : ProjectsLayoutMode.Cards;

        Assert.Equal(expected, (await store.LoadAsync()).LayoutMode);
    }

    [Fact]
    public async Task LoadAsync_ALayoutNameThisBuildDoesNotKnow_FallsBackToCards()
    {
        await File.WriteAllTextAsync(_configFilePath, """{ "ProjectsDisplay": { "LayoutMode": "Mosaic" } }""");
        var store = new ProjectsDisplaySettingsStore(_configFilePath);

        Assert.Equal(ProjectsLayoutMode.Cards, (await store.LoadAsync()).LayoutMode);
    }

    [Fact]
    public async Task SaveAsync_LeavesTheOtherSectionsIntact()
    {
        var layoutStore = new LayoutSettingsStore(_configFilePath);
        await layoutStore.SaveAsync(new LayoutSettings { SingleSessionLayout = true });

        var store = new ProjectsDisplaySettingsStore(_configFilePath);
        await store.SaveAsync(new ProjectsDisplaySettings { LayoutMode = ProjectsLayoutMode.List });

        Assert.True((await layoutStore.LoadAsync()).SingleSessionLayout);
        Assert.Equal(ProjectsLayoutMode.List, (await store.LoadAsync()).LayoutMode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
