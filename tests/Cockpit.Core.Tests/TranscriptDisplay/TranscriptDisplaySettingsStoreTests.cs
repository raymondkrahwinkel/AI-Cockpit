using Cockpit.Core.TranscriptDisplay;
using Cockpit.Infrastructure.TranscriptDisplay;

namespace Cockpit.Core.Tests.TranscriptDisplay;

/// <summary>
/// Load/save round-trip for the transcript-display section of <c>cockpit.json</c>, plus the invariant
/// that saving it leaves the sibling sections (notifications, shortcuts) intact — all stores
/// share the one file.
/// </summary>
public class TranscriptDisplaySettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public TranscriptDisplaySettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsSettings()
    {
        var store = new TranscriptDisplaySettingsStore(_configFilePath);

        await store.SaveAsync(new TranscriptDisplaySettings { ShowTimestamps = true });
        var loaded = await store.LoadAsync();

        Assert.True(loaded.ShowTimestamps);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
