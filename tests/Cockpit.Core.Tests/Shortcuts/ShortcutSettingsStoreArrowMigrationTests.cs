using Cockpit.Core.Shortcuts;
using Cockpit.Infrastructure.Shortcuts;
using FluentAssertions;

namespace Cockpit.Core.Tests.Shortcuts;

/// <summary>
/// The session switch used to default to Ctrl+Up / Ctrl+Down; those bare Ctrl+arrows now drive spatial pane
/// focus, and the switch moved to Ctrl+Shift+Up/Down (AC-31). A config that saved the old defaults explicitly
/// would double-bind Ctrl+Up with the new "focus pane up", so the store migrates exactly those two values on
/// load — while leaving a gesture the operator changed to anything else untouched.
/// </summary>
public class ShortcutSettingsStoreArrowMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public ShortcutSettingsStoreArrowMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task LoadAsync_SavedOldCtrlArrowSessionSwitch_MigratesToCtrlShiftArrow()
    {
        await File.WriteAllTextAsync(_configFilePath, """
            {
              "Shortcuts": { "Gestures": { "PreviousSession": "Ctrl+Up", "NextSession": "Ctrl+Down" } }
            }
            """);
        var store = new ShortcutSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        settings.GestureFor(ShortcutAction.PreviousSession).Should().Be("Ctrl+Shift+Up");
        settings.GestureFor(ShortcutAction.NextSession).Should().Be("Ctrl+Shift+Down");
    }

    [Fact]
    public async Task LoadAsync_ADeliberatelyDifferentSessionSwitch_IsLeftAlone()
    {
        await File.WriteAllTextAsync(_configFilePath, """
            {
              "Shortcuts": { "Gestures": { "PreviousSession": "Alt+Up" } }
            }
            """);
        var store = new ShortcutSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        settings.GestureFor(ShortcutAction.PreviousSession).Should().Be("Alt+Up");
    }

    [Fact]
    public async Task LoadAsync_AFreshConfig_TakesTheNewDefaults_WithoutDoubleBinding()
    {
        var store = new ShortcutSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        settings.GestureFor(ShortcutAction.PreviousSession).Should().Be("Ctrl+Shift+Up");
        settings.GestureFor(ShortcutAction.NextSession).Should().Be("Ctrl+Shift+Down");
        settings.GestureFor(ShortcutAction.FocusPaneUp).Should().Be("Ctrl+Alt+Up");
        settings.GestureFor(ShortcutAction.FocusPaneDown).Should().Be("Ctrl+Alt+Down");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
