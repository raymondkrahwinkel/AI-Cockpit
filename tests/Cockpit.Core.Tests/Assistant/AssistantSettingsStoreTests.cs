using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Layout;
using Cockpit.Core.Layout;

namespace Cockpit.Core.Tests.Assistant;

/// <summary>Load/save round-trip for the assistant section of <c>cockpit.json</c> (AC-543).</summary>
public class AssistantSettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public AssistantSettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    // Criterion 1 / decision 7: a fresh install has the assistant off — no instance, no model, nothing costing
    // anything — until the operator turns it on.
    [Fact]
    public async Task LoadAsync_NoConfigFile_IsDisabledByDefault()
    {
        var store = new AssistantSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        Assert.False(settings.IsEnabled);
        Assert.True(settings.SpeakReplies);
        Assert.Equal("F10", settings.PushToTalkKeyName);
        Assert.False(settings.AlwaysOnCostAcknowledged);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsAllFields()
    {
        var store = new AssistantSettingsStore(_configFilePath);

        await store.SaveAsync(new AssistantSettings
        {
            IsEnabled = true,
            SpeakReplies = false,
            PushToTalkKeyName = "F11",
            AlwaysOnCostAcknowledged = true,
        });
        var loaded = await store.LoadAsync();

        Assert.True(loaded.IsEnabled);
        Assert.False(loaded.SpeakReplies);
        Assert.Equal("F11", loaded.PushToTalkKeyName);
        Assert.True(loaded.AlwaysOnCostAcknowledged);
    }

    // Criterion 9: speaking and being enabled are two separate decisions — turning the assistant on must not
    // silently turn speech on too, and turning speech off must not silently disable the assistant.
    [Fact]
    public async Task SaveAsync_SpeakRepliesIsIndependentOfIsEnabled()
    {
        var store = new AssistantSettingsStore(_configFilePath);

        await store.SaveAsync(new AssistantSettings { IsEnabled = true, SpeakReplies = false });
        var loaded = await store.LoadAsync();

        Assert.True(loaded.IsEnabled);
        Assert.False(loaded.SpeakReplies);
    }

    [Fact]
    public async Task SaveAsync_LeavesTheOtherSectionsIntact()
    {
        var layoutStore = new LayoutSettingsStore(_configFilePath);
        await layoutStore.SaveAsync(new LayoutSettings { SingleSessionLayout = true });

        var assistantStore = new AssistantSettingsStore(_configFilePath);
        await assistantStore.SaveAsync(new AssistantSettings { IsEnabled = true });

        Assert.True((await layoutStore.LoadAsync()).SingleSessionLayout);
        Assert.True((await assistantStore.LoadAsync()).IsEnabled);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
