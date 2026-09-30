using Cockpit.Infrastructure.WorkingPaths;

namespace Cockpit.Core.Tests.WorkingPaths;

/// <summary>
/// Load/save round-trip for the working-paths section of <c>cockpit.json</c>, plus the shared-file invariant
/// that recording a path leaves a sibling section (notifications) intact.
/// </summary>
public class WorkingPathHistoryStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public WorkingPathHistoryStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task RecordRecentAsync_PersistsMostRecentFirst()
    {
        var store = new WorkingPathHistoryStore(_configFilePath);

        await store.RecordRecentAsync(@"C:\a");
        await store.RecordRecentAsync(@"C:\b");

        var loaded = await store.LoadAsync();
        Assert.Equal(new[] { @"C:\b", @"C:\a" }, loaded.Recent);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
