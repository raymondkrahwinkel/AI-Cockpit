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
        // AC-138 follow-up: the chat window's reading level defaults to the same level a fresh SDK session
        // opens at, so turning the assistant on for the first time never shows a level nobody chose.
        Assert.Equal(ReadingLevel.Developer, settings.ReadingLevel);

        // AC-575: no source is ticked one at a time until the operator says so. AC-637: allow-all is on above
        // them, so a fresh install skips the card for everything the assistant asks — the surfaces that report
        // `HasConsentBypass` say so from the first start rather than only once something was ticked.
        Assert.Empty(settings.ConsentBypassSources);
        Assert.Empty(settings.ConsentBypassDangerousSources);
        Assert.True(settings.ConsentBypassAll);
        Assert.True(settings.HasConsentBypass);
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
        Assert.Equal(ReadingLevel.Simple, loaded.ReadingLevel);
        Assert.Equal(["Terminal MCP", "cockpit-kubernetes"], loaded.ConsentBypassSources);
        Assert.Equal(["cockpit-kubernetes"], loaded.ConsentBypassDangerousSources);
        // Switched off against its default, so this round trip proves the off is stored rather than re-defaulted.
        Assert.False(loaded.ConsentBypassAll);
    }

    /// <summary>
    /// A reading level name this build does not recognise — a hand edit, or a newer build's fourth level — must
    /// not cost the whole <c>assistant</c> section the way an unparsed non-nullable enum would (see
    /// <c>AssistantSettingsEntry.ReadingLevel</c>'s doc-comment). It reads back as the app default instead.
    /// </summary>
    [Fact]
    public async Task LoadAsync_AnUnrecognisedReadingLevelName_FallsBackToDeveloper()
    {
        await File.WriteAllTextAsync(
            _configFilePath,
            """{"Assistant":{"IsEnabled":true,"SpeakReplies":true,"PushToTalkKeyName":"F10","ReadingLevel":"Verbose"}}""");

        var loaded = await new AssistantSettingsStore(_configFilePath).LoadAsync();

        Assert.True(loaded.IsEnabled);
        Assert.Equal(ReadingLevel.Developer, loaded.ReadingLevel);
    }

    /// <summary>
    /// A config written before #AC-575, or edited by hand, has no bypass lists at all. Those lists must read as
    /// empty — a source is on them because someone ticked it, never because a value was missing. Two string lists
    /// were chosen over one enum per source precisely so this direction is the safe one: an absent list is empty,
    /// and a name this build does not recognise is a name that matches no source.
    /// </summary>
    [Fact]
    public async Task LoadAsync_AConfigWithNoBypassSection_TicksNoSourceOfItsOwn()
    {
        await File.WriteAllTextAsync(
            _configFilePath,
            """{"Assistant":{"IsEnabled":true,"SpeakReplies":true,"PushToTalkKeyName":"F10","ConsentBypassSources":null}}""");

        var loaded = await new AssistantSettingsStore(_configFilePath).LoadAsync();

        Assert.True(loaded.IsEnabled);
        Assert.Empty(loaded.ConsentBypassSources);
        Assert.Empty(loaded.ConsentBypassDangerousSources);
    }

    /// <summary>
    /// #AC-637's upgrade direction, written down because it is the one place this change is not visible: an
    /// <c>assistant</c> section from before this build has no <c>ConsentBypassAll</c> property, and reads back
    /// <em>on</em> — the same state a fresh install is in, rather than a second, quieter default for anyone who
    /// happened to have the config already.
    /// </summary>
    [Fact]
    public async Task LoadAsync_AConfigFromBeforeAllowAll_ReadsBackWithItOn()
    {
        await File.WriteAllTextAsync(
            _configFilePath,
            """{"Assistant":{"IsEnabled":true,"SpeakReplies":true,"PushToTalkKeyName":"F10","ConsentBypassSources":[]}}""");

        var loaded = await new AssistantSettingsStore(_configFilePath).LoadAsync();

        Assert.True(loaded.ConsentBypassAll);
        Assert.True(loaded.HasConsentBypass);
    }

    [Fact]
    public async Task SaveAsync_AllowAllSwitchedOff_StaysOff()
    {
        // The direction that matters: a default of true must not read an operator's deliberate off back as on.
        var store = new AssistantSettingsStore(_configFilePath);

        await store.SaveAsync(new AssistantSettings { IsEnabled = true, ConsentBypassAll = false });

        var loaded = await store.LoadAsync();
        Assert.False(loaded.ConsentBypassAll);
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
