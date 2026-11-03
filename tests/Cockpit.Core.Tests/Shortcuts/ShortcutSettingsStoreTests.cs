using Cockpit.Core.Shortcuts;
using Cockpit.Infrastructure.Shortcuts;

namespace Cockpit.Core.Tests.Shortcuts;

/// <summary>
/// Load/save round-trip for the shortcuts section of <c>cockpit.json</c>, and the migrations the store applies on
/// load to a file an older build wrote.
/// </summary>
public class ShortcutSettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public ShortcutSettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsAChangedGesture()
    {
        var store = new ShortcutSettingsStore(_configFilePath);

        await store.SaveAsync(ShortcutSettings.Default.With(ShortcutAction.Options, "Ctrl+Shift+O"));
        var loaded = await store.LoadAsync();

        Assert.Equal("Ctrl+Shift+O", loaded.GestureFor(ShortcutAction.Options));
    }

    /// <summary>
    /// Row 1 (AC-31): bare Ctrl+arrows now drive pane focus, so a saved old session-switch default would double-bind.
    /// Rows 2-3: the old on/off-plus-modifier setting; an operator who picked Alt, or switched it off, must not be
    /// back on Ctrl after upgrading. Row 4 (AC-401): the old Ctrl+B zoom default, which a focused terminal swallows,
    /// is written out by every Save — and saving the migrated set must keep the migration.
    /// </summary>
    [Theory]
    [InlineData("""{ "Shortcuts": { "Gestures": { "PreviousSession": "Ctrl+Up", "NextSession": "Ctrl+Down" } } }""", ShortcutAction.PreviousSession, "Ctrl+Shift+Up")]
    [InlineData("""{ "SessionSwitching": { "IsEnabled": true, "Modifier": "Alt" } }""", ShortcutAction.PreviousSession, "Alt+Up")]
    [InlineData("""{ "SessionSwitching": { "IsEnabled": false, "Modifier": "Ctrl" } }""", ShortcutAction.PreviousSession, "")]
    [InlineData("""{ "Shortcuts": { "Gestures": { "ToggleZoom": "Ctrl+B" } } }""", ShortcutAction.ToggleZoom, "Ctrl+Shift+M")]
    public async Task LoadAsync_AGestureAnOlderBuildWrote_IsMigrated(string json, ShortcutAction action, string expected)
    {
        await File.WriteAllTextAsync(_configFilePath, json);
        var store = new ShortcutSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        Assert.Equal(expected, settings.GestureFor(action));

        if (expected.Length > 0)
        {
            await store.SaveAsync(settings);
            Assert.Equal(expected, (await store.LoadAsync()).GestureFor(action));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
