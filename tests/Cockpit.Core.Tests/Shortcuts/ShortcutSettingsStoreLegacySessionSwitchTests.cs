using Cockpit.Core.Shortcuts;
using Cockpit.Infrastructure.Shortcuts;
using FluentAssertions;

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

        settings.GestureFor(ShortcutAction.PreviousSession).Should().Be("Alt+Up");
        settings.GestureFor(ShortcutAction.NextSession).Should().Be("Alt+Down");
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

        settings.GestureFor(ShortcutAction.PreviousSession).Should().BeEmpty();
        settings.GestureFor(ShortcutAction.NextSession).Should().BeEmpty();
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

        settings.GestureFor(ShortcutAction.NextSession).Should().Be("Ctrl+Shift+Down");
    }

    [Fact]
    public async Task LoadAsync_NoLegacySection_UsesTheCatalogDefaults()
    {
        var store = new ShortcutSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        settings.GestureFor(ShortcutAction.PreviousSession).Should().Be("Ctrl+Shift+Up");
        settings.GestureFor(ShortcutAction.NextSession).Should().Be("Ctrl+Shift+Down");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
