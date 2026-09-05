using Cockpit.Core.Shortcuts;
using Cockpit.Infrastructure.Shortcuts;

namespace Cockpit.Core.Tests.Shortcuts;

/// <summary>
/// The session switch used to be its own setting (a master on/off plus a modifier) and is now two ordinary
/// shortcuts. A <c>cockpit.json</c> written by an older build still carries that <c>sessionSwitching</c>
/// section, so the store translates it into gestures on load — otherwise an operator who had picked Alt (or
/// switched the gesture off) would silently be back on Ctrl after upgrading.
/// </summary>
public class ShortcutSettingsStoreLegacySessionSwitchTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public ShortcutSettingsStoreLegacySessionSwitchTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task LoadAsync_LegacyAltModifier_BecomesTheAltSessionSwitchGestures()
    {
        await File.WriteAllTextAsync(_configFilePath, """
            {
              "SessionSwitching": { "IsEnabled": true, "Modifier": "Alt" }
            }
            """);
        var store = new ShortcutSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        Assert.Equal("Alt+Up", settings.GestureFor(ShortcutAction.PreviousSession));
        Assert.Equal("Alt+Down", settings.GestureFor(ShortcutAction.NextSession));
    }

    [Fact]
    public async Task LoadAsync_LegacySwitchDisabled_LeavesTheSessionSwitchUnbound()
    {
        await File.WriteAllTextAsync(_configFilePath, """
            {
              "SessionSwitching": { "IsEnabled": false, "Modifier": "Ctrl" }
            }
            """);
        var store = new ShortcutSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        Assert.Empty(settings.GestureFor(ShortcutAction.PreviousSession));
        Assert.Empty(settings.GestureFor(ShortcutAction.NextSession));
    }

    [Fact]
    public async Task LoadAsync_WhenTheOperatorAlreadyRebound_TheLegacySectionIsIgnored()
    {
        // A gesture saved since the migration wins: the legacy section lingers in the file, but it must not
        // overwrite a deliberate choice.
        await File.WriteAllTextAsync(_configFilePath, """
            {
              "SessionSwitching": { "IsEnabled": true, "Modifier": "Alt" },
              "Shortcuts": { "Gestures": { "NextSession": "Ctrl+Shift+Down" } }
            }
            """);
        var store = new ShortcutSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        Assert.Equal("Ctrl+Shift+Down", settings.GestureFor(ShortcutAction.NextSession));
    }

    [Fact]
    public async Task LoadAsync_LegacyAltModifier_WithAnUnrelatedShortcutSaved_StillCarriesOver()
    {
        // AC-35: the mere existence of a shortcuts section — here only an unrelated action (CommandPalette) was
        // ever rebound — must not suppress the legacy carry-over. The operator never saved a session-switch
        // gesture, so their legacy Alt choice must still win over the catalog default.
        await File.WriteAllTextAsync(_configFilePath, """
            {
              "SessionSwitching": { "IsEnabled": true, "Modifier": "Alt" },
              "Shortcuts": { "Gestures": { "CommandPalette": "Ctrl+K" } }
            }
            """);
        var store = new ShortcutSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        Assert.Equal("Alt+Up", settings.GestureFor(ShortcutAction.PreviousSession));
        Assert.Equal("Alt+Down", settings.GestureFor(ShortcutAction.NextSession));
        Assert.Equal("Ctrl+K", settings.GestureFor(ShortcutAction.CommandPalette));
    }

    [Fact]
    public async Task LoadAsync_LegacySwitchDisabled_WithAnUnrelatedShortcutSaved_StillUnbindsTheSessionSwitch()
    {
        // The disabled-legacy path has the same gate, so it too must ignore an unrelated saved shortcut (AC-35):
        // an operator who turned the session switch off keeps it off, not reset to the catalog default.
        await File.WriteAllTextAsync(_configFilePath, """
            {
              "SessionSwitching": { "IsEnabled": false, "Modifier": "Ctrl" },
              "Shortcuts": { "Gestures": { "CommandPalette": "Ctrl+K" } }
            }
            """);
        var store = new ShortcutSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        Assert.Empty(settings.GestureFor(ShortcutAction.PreviousSession));
        Assert.Empty(settings.GestureFor(ShortcutAction.NextSession));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
