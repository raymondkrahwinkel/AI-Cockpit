using Cockpit.Core.Debugging;
using Cockpit.Core.Diagnostics;
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

        await File.WriteAllTextAsync(_configFilePath, "{\"debug\":{}}");
        var defaults = await store.LoadAsync();
        Assert.Equal(5, defaults.UiFreezeThresholdSeconds);
        Assert.Equal(15, defaults.RenderStallThresholdSeconds);

        await store.SaveAsync(new DebugSettings
        {
            ShowDebugControls = true,
            LogDiagnosticSnapshots = true,
            UiFreezeThresholdSeconds = 9,
            RenderStallThresholdSeconds = 25,
        });
        var loaded = await store.LoadAsync();

        Assert.True(loaded.ShowDebugControls);
        Assert.True(loaded.LogDiagnosticSnapshots);
        Assert.Equal(9, loaded.UiFreezeThresholdSeconds);
        Assert.Equal(25, loaded.RenderStallThresholdSeconds);
        Assert.False(UiThreadHeartbeat.Decide(TimeSpan.FromSeconds(6), false, TimeSpan.FromSeconds(loaded.UiFreezeThresholdSeconds)).Warn);
        Assert.True(UiThreadHeartbeat.Decide(TimeSpan.FromSeconds(10), false, TimeSpan.FromSeconds(loaded.UiFreezeThresholdSeconds)).Warn);
        Assert.False(RenderClockHeartbeat.Decide(TimeSpan.FromSeconds(20), false, TimeSpan.FromSeconds(loaded.RenderStallThresholdSeconds)).Stalled);
        Assert.True(RenderClockHeartbeat.Decide(TimeSpan.FromSeconds(26), false, TimeSpan.FromSeconds(loaded.RenderStallThresholdSeconds)).Stalled);
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
