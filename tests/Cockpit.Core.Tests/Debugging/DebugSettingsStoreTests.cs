using Cockpit.Core.Debugging;
using Cockpit.Core.SessionBehavior;
using Cockpit.Infrastructure.Debugging;
using Cockpit.Infrastructure.SessionBehavior;

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
    public async Task LoadAsync_NoConfigFile_HidesControlsButLogsSnapshotsByDefault()
    {
        var store = new DebugSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        Assert.False(settings.ShowDebugControls);
        Assert.True(settings.LogDiagnosticSnapshots); // AC-1125: on by default, so a fresh install has heap history from the first freeze.
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

    [Fact]
    public async Task SaveAsync_LeavesTheOtherSectionsIntact()
    {
        var behaviorStore = new SessionBehaviorSettingsStore(_configFilePath);
        await behaviorStore.SaveAsync(new SessionBehaviorSettings { AutoCloseOnExit = true });

        var debugStore = new DebugSettingsStore(_configFilePath);
        await debugStore.SaveAsync(new DebugSettings { ShowDebugControls = true });

        Assert.True((await behaviorStore.LoadAsync()).AutoCloseOnExit);
        Assert.True((await debugStore.LoadAsync()).ShowDebugControls);
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
