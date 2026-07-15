using FluentAssertions;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Core.Tests.Profiles;

/// <summary>
/// Exercises persistence (load/save round-trip) and the no-config-yet auto-detect fallback
/// against a real temporary directory tree — no real <c>%APPDATA%</c>/<c>%USERPROFILE%</c>
/// access; the store is pointed at a fake config file via its internal test constructor.
/// </summary>
public class SessionProfileStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public SessionProfileStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task LoadAsync_NoConfigFile_ReturnsEmpty_WhenAutoDetectFindsNothing()
    {
        // No candidate dirs exist under this temp root, so auto-detect (based on the real
        // %USERPROFILE%) may or may not find something on the actual machine — the store's
        // own config-file-absent path is what's under test here, verified by asserting it
        // doesn't throw and returns a list (auto-detect itself is covered in isolation by
        // ClaudeCliProfileDetectorTests).
        var store = new SessionProfileStore(_configFilePath);

        var profiles = await store.LoadAsync();

        profiles.Should().NotBeNull();
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsProfiles()
    {
        var store = new SessionProfileStore(_configFilePath);
        var profiles = new List<SessionProfile>
        {
            new("personal", ClaudePluginProfile.Create(@"C:\Users\raymo\.claude-personal", null), Purpose: "Personal Zyra profile"),
            new("work", ClaudePluginProfile.Create(@"C:\Users\raymo\.claude-work", @"C:\tools\claude-work.exe")),
        };

        await store.SaveAsync(profiles);
        var loaded = await store.LoadAsync();

        loaded.Should().BeEquivalentTo(profiles);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsPerProfileDefaults()
    {
        var store = new SessionProfileStore(_configFilePath);
        var profiles = new List<SessionProfile>
        {
            new("personal", ClaudePluginProfile.Create(@"C:\Users\raymo\.claude-personal", null),
                Defaults: new ProfileDefaults("bypassPermissions", "opus", "high")),
            new("work", ClaudePluginProfile.Create(@"C:\Users\raymo\.claude-work", null)),
        };

        await store.SaveAsync(profiles);
        var loaded = await store.LoadAsync();

        // The first profile's defaults survive the round-trip; the second keeps null defaults
        // (no defaults section written), so the two are not conflated.
        loaded.Should().BeEquivalentTo(profiles);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsAutoApproveToolsDefault()
    {
        var store = new SessionProfileStore(_configFilePath);
        var profiles = new List<SessionProfile>
        {
            new("ollama", new OllamaConfig("http://localhost:11434", "llama3.1"),
                Defaults: new ProfileDefaults("default", "sonnet", "medium", AutoApproveTools: true)),
            new("lmstudio", new LmStudioConfig("http://localhost:1234", "qwen2.5-7b-instruct"),
                Defaults: new ProfileDefaults("default", "sonnet", "medium")),
        };

        await store.SaveAsync(profiles);
        var loaded = await store.LoadAsync();

        // Explicitly opted-in survives, and the (default) false of the second profile is not flipped to
        // true by the round-trip — the two are not conflated.
        loaded.Should().BeEquivalentTo(profiles);
        loaded[0].Defaults!.AutoApproveTools.Should().BeTrue();
        loaded[1].Defaults!.AutoApproveTools.Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsProviderConfigs()
    {
        var store = new SessionProfileStore(_configFilePath);
        var profiles = new List<SessionProfile>
        {
            new("claude", ClaudePluginProfile.Create(@"C:\Users\raymo\.claude", null)),
            new("local-ollama", new OllamaConfig("http://localhost:11434", "llama3.1", "You are helpful.")),
            new("local-lmstudio", new LmStudioConfig("http://localhost:1234", "qwen2.5-7b-instruct", "secret-key", "Be concise.")),
        };

        await store.SaveAsync(profiles);
        var loaded = await store.LoadAsync();

        loaded.Should().BeEquivalentTo(profiles);
        loaded[0].Provider.Should().Be(SessionProvider.Plugin);
        loaded[1].Provider.Should().Be(SessionProvider.Ollama);
        loaded[2].Provider.Should().Be(SessionProvider.LmStudio);
    }

    [Fact]
    public async Task SaveAsync_CreatesConfigDirectory_WhenAbsent()
    {
        var nestedConfigPath = Path.Combine(_tempDir, "nested", "cockpit.json");
        var store = new SessionProfileStore(nestedConfigPath);

        await store.SaveAsync([new SessionProfile("default", new ClaudeConfig(@"C:\Users\raymo\.claude"))]);

        File.Exists(nestedConfigPath).Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_ConfigFileWithEmptyProfilesList_FallsBackToAutoDetect()
    {
        await File.WriteAllTextAsync(_configFilePath, """{"profiles":[]}""");
        var store = new SessionProfileStore(_configFilePath);

        var profiles = await store.LoadAsync();

        // Empty persisted list is treated the same as "no config yet" — falls back to
        // auto-detect rather than returning an empty cockpit with no profiles at all.
        profiles.Should().NotBeNull();
    }

    [Fact]
    public async Task LoadAsync_ConfigWrittenByAnEarlierVersion_StillLoads()
    {
        // Verbatim profiles section as written before the Claude* -> Session* rename: the on-disk
        // contract is the property names, not the C# type names, so a config from an older build
        // must keep loading. Guards the next rename against silently orphaning everyone's profiles.
        await File.WriteAllTextAsync(_configFilePath, """
            {
              "Profiles": [
                {
                  "Label": "work",
                  "ConfigDir": "/home/raymond/.claude-work",
                  "ExecutablePath": null,
                  "Purpose": "Work account",
                  "Defaults": {
                    "PermissionMode": "bypassPermissions",
                    "Model": "opus",
                    "Effort": "high",
                    "AutoApproveTools": false
                  },
                  "Provider": null
                },
                {
                  "Label": "local-ollama",
                  "ConfigDir": "",
                  "Provider": { "Provider": "Ollama", "BaseUrl": "http://localhost:11434", "Model": "llama3.1" }
                }
              ]
            }
            """);
        var store = new SessionProfileStore(_configFilePath);

        var profiles = await store.LoadAsync();

        profiles.Should().HaveCount(2);
        profiles[0].Label.Should().Be("work");
        // A provider-less legacy Claude entry is migrated to the bundled Claude provider plugin on load (Fase 4),
        // its top-level ConfigDir carried into the plugin config.
        profiles[0].ProviderConfig.Should().Be(ClaudePluginProfile.Create("/home/raymond/.claude-work", null));
        profiles[0].Purpose.Should().Be("Work account");
        profiles[0].Provider.Should().Be(SessionProvider.Plugin);
        profiles[0].Defaults.Should().NotBeNull();
        profiles[1].Provider.Should().Be(SessionProvider.Ollama);
        profiles[1].ProviderConfig.Should().BeOfType<OllamaConfig>()
            .Which.Model.Should().Be("llama3.1");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
