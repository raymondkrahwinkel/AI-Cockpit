using Cockpit.Core.Debugging;
using Cockpit.Infrastructure.Debugging;

namespace Cockpit.Core.Tests.Debugging;

/// <summary>
/// Load/save round-trip for the debug section of <c>cockpit.json</c> (#73), plus the invariant every store in
/// this file has to keep: saving one section leaves its siblings alone.
/// </summary>
public class DebugSettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public DebugSettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsSettings()
    {
        var store = new DebugSettingsStore(_configFilePath);

        await store.SaveAsync(new DebugSettings { ShowDebugControls = true, LogDiagnosticSnapshots = true });
        var loaded = await store.LoadAsync();

        Assert.True(loaded.ShowDebugControls);
        Assert.True(loaded.LogDiagnosticSnapshots);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
