using Cockpit.Core.Assistant;
using Cockpit.Core.Sessions;
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
            AlwaysOnTop = false,
            ContextCompactionPercent = 30,
            ReadingLevel = ReadingLevel.Simple,
            ConsentBypassSources = ["Terminal MCP", "cockpit-kubernetes"],
            ConsentBypassDangerousSources = ["cockpit-kubernetes"],
            ConsentBypassAll = false,
        });
        var loaded = await store.LoadAsync();

        Assert.True(loaded.IsEnabled);
        Assert.False(loaded.SpeakReplies);
        Assert.Equal("F11", loaded.PushToTalkKeyName);
        Assert.True(loaded.AlwaysOnCostAcknowledged);
        // AC-681: switched off against its default (true), so this round trip proves the off is stored rather
        // than re-defaulted — which is exactly what silently dropped it (see AssistantSettingsEntry).
        Assert.False(loaded.AlwaysOnTop);
        Assert.Equal(30, loaded.ContextCompactionPercent);
        Assert.Equal(ReadingLevel.Simple, loaded.ReadingLevel);
        Assert.Equal(["Terminal MCP", "cockpit-kubernetes"], loaded.ConsentBypassSources);
        Assert.Equal(["cockpit-kubernetes"], loaded.ConsentBypassDangerousSources);
        // Switched off against its default, so this round trip proves the off is stored rather than re-defaulted.
        Assert.False(loaded.ConsentBypassAll);
    }

    /// <summary>
    /// #AC-637's upgrade direction, and the one place the default-on deliberately does not reach: an
    /// <c>assistant</c> section from before this build has no <c>ConsentBypassAll</c> property, and reads back
    /// <em>off</em>. That cockpit was asking about everything, so it keeps asking — upgrading is not the moment to
    /// widen a permission nobody asked to widen. Only a fresh install, with no section to read at all, gets the
    /// record's own default.
    /// </summary>
    [Fact]
    public async Task LoadAsync_AConfigFromBeforeAllowAll_KeepsAskingRatherThanWidening()
    {
        await File.WriteAllTextAsync(
            _configFilePath,
            """{"Assistant":{"IsEnabled":true,"SpeakReplies":true,"PushToTalkKeyName":"F10","ConsentBypassSources":[]}}""");

        var loaded = await new AssistantSettingsStore(_configFilePath).LoadAsync();

        Assert.False(loaded.ConsentBypassAll);
        Assert.False(loaded.HasConsentBypass);
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
