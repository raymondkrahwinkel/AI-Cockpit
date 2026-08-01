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

        // AC-575: nothing is exempt from the consent card until the operator says so.
        Assert.Empty(settings.ConsentBypassSources);
        Assert.Empty(settings.ConsentBypassDangerousSources);
        Assert.False(settings.HasConsentBypass);
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
            ConsentBypassSources = ["Terminal MCP", "cockpit-kubernetes"],
            ConsentBypassDangerousSources = ["cockpit-kubernetes"],
        });
        var loaded = await store.LoadAsync();

        Assert.True(loaded.IsEnabled);
        Assert.False(loaded.SpeakReplies);
        Assert.Equal("F11", loaded.PushToTalkKeyName);
        Assert.True(loaded.AlwaysOnCostAcknowledged);
        Assert.Equal(["Terminal MCP", "cockpit-kubernetes"], loaded.ConsentBypassSources);
        Assert.Equal(["cockpit-kubernetes"], loaded.ConsentBypassDangerousSources);
    }

    /// <summary>
    /// A config written before #AC-575, or edited by hand, has no bypass lists at all. It must read as "nothing is
    /// exempt" — the least powerful answer — rather than as a missing value some default fills in. Two string lists
    /// were chosen over one enum per source precisely so this direction is the safe one: an absent list is empty,
    /// and a name this build does not recognise is a name that matches no source.
    /// </summary>
    [Fact]
    public async Task LoadAsync_AConfigWithNoBypassSection_ExemptsNothing()
    {
        await File.WriteAllTextAsync(
            _configFilePath,
            """{"Assistant":{"IsEnabled":true,"SpeakReplies":true,"PushToTalkKeyName":"F10","ConsentBypassSources":null}}""");

        var loaded = await new AssistantSettingsStore(_configFilePath).LoadAsync();

        Assert.True(loaded.IsEnabled);
        Assert.Empty(loaded.ConsentBypassSources);
        Assert.Empty(loaded.ConsentBypassDangerousSources);
        Assert.False(loaded.HasConsentBypass);
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
