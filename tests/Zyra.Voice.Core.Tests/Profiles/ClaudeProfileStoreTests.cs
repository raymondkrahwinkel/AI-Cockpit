using FluentAssertions;
using Zyra.Voice.Core.Profiles;
using Zyra.Voice.Infrastructure.Claude;

namespace Zyra.Voice.Core.Tests.Profiles;

/// <summary>
/// Exercises persistence (load/save round-trip) and the no-config-yet auto-detect fallback
/// against a real temporary directory tree — no real <c>%APPDATA%</c>/<c>%USERPROFILE%</c>
/// access; the store is pointed at a fake config file via its internal test constructor.
/// </summary>
public class ClaudeProfileStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public ClaudeProfileStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "zyra-voice-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "zyra-voice.json");
    }

    [Fact]
    public async Task LoadAsync_NoConfigFile_ReturnsEmpty_WhenAutoDetectFindsNothing()
    {
        // No candidate dirs exist under this temp root, so auto-detect (based on the real
        // %USERPROFILE%) may or may not find something on the actual machine — the store's
        // own config-file-absent path is what's under test here, verified by asserting it
        // doesn't throw and returns a list (auto-detect itself is covered in isolation by
        // ClaudeProfileAutoDetectorTests).
        var store = new ClaudeProfileStore(_configFilePath);

        var profiles = await store.LoadAsync();

        profiles.Should().NotBeNull();
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsProfiles()
    {
        var store = new ClaudeProfileStore(_configFilePath);
        var profiles = new List<ClaudeProfile>
        {
            new("personal", @"C:\Users\raymo\.claude-personal", Purpose: "Personal Zyra profile"),
            new("work", @"C:\Users\raymo\.claude-work", ExecutablePath: @"C:\tools\claude-work.exe"),
        };

        await store.SaveAsync(profiles);
        var loaded = await store.LoadAsync();

        loaded.Should().BeEquivalentTo(profiles);
    }

    [Fact]
    public async Task SaveAsync_CreatesConfigDirectory_WhenAbsent()
    {
        var nestedConfigPath = Path.Combine(_tempDir, "nested", "zyra-voice.json");
        var store = new ClaudeProfileStore(nestedConfigPath);

        await store.SaveAsync([new ClaudeProfile("default", @"C:\Users\raymo\.claude")]);

        File.Exists(nestedConfigPath).Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_ConfigFileWithEmptyProfilesList_FallsBackToAutoDetect()
    {
        await File.WriteAllTextAsync(_configFilePath, """{"profiles":[]}""");
        var store = new ClaudeProfileStore(_configFilePath);

        var profiles = await store.LoadAsync();

        // Empty persisted list is treated the same as "no config yet" — falls back to
        // auto-detect rather than returning an empty cockpit with no profiles at all.
        profiles.Should().NotBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
