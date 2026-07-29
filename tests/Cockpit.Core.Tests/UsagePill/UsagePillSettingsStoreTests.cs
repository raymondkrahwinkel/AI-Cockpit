using Cockpit.Core.UsagePill;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.TranscriptDisplay;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Infrastructure.UsagePill;

namespace Cockpit.Core.Tests.UsagePill;

/// <summary>
/// Load/save round-trip for the usage-pill section of <c>cockpit.json</c> (AC-105): the default is just the
/// context window, the selection survives a round-trip, an on-disk name this build no longer knows is dropped
/// rather than throwing, and saving it leaves a sibling section intact — all stores share the one file.
/// </summary>
public class UsagePillSettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public UsagePillSettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task LoadAsync_NoConfigFile_ReturnsContextOnlyDefault()
    {
        var store = new UsagePillSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        Assert.Equal(new[] { UsagePillField.Context }, settings.VisibleFields);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsTheSelectionInOrder()
    {
        var store = new UsagePillSettingsStore(_configFilePath);

        await store.SaveAsync(new UsagePillSettings
        {
            VisibleFields = [UsagePillField.WeeklyWindow, UsagePillField.Context, UsagePillField.SessionUsage],
        });
        var loaded = await store.LoadAsync();

        Assert.Equal(
            new[] { UsagePillField.WeeklyWindow, UsagePillField.Context, UsagePillField.SessionUsage },
            loaded.VisibleFields);
    }

    [Fact]
    public void Entry_ToDomain_DropsAnUnknownFieldName()
    {
        var entry = new UsagePillSettingsEntry { VisibleFields = ["Context", "SomethingRemovedSince", "WeeklyWindow"] };

        Assert.Equal(new[] { UsagePillField.Context, UsagePillField.WeeklyWindow }, entry.ToDomain().VisibleFields);
    }

    [Fact]
    public async Task SaveAsync_LeavesTheOtherSectionsIntact()
    {
        var displayStore = new TranscriptDisplaySettingsStore(_configFilePath);
        await displayStore.SaveAsync(new TranscriptDisplaySettings { ShowTimestamps = true });

        var usagePillStore = new UsagePillSettingsStore(_configFilePath);
        await usagePillStore.SaveAsync(new UsagePillSettings { VisibleFields = [UsagePillField.SessionUsage] });

        var reloadedDisplay = await displayStore.LoadAsync();
        var reloadedUsagePill = await usagePillStore.LoadAsync();

        Assert.True(reloadedDisplay.ShowTimestamps);
        Assert.Equal(new[] { UsagePillField.SessionUsage }, reloadedUsagePill.VisibleFields);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
