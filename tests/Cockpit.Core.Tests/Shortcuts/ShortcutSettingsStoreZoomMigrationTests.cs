using Cockpit.Core.Shortcuts;
using Cockpit.Infrastructure.Shortcuts;

namespace Cockpit.Core.Tests.Shortcuts;

/// <summary>
/// Toggle zoom used to default to Ctrl+B, which a focused terminal swallows (tmux prefix, readline
/// backward-char) — and a zoomed pane is exactly when the terminal has focus. The default moved to
/// Ctrl+Shift+M (AC-401). Anyone who ever pressed Save in Options → Shortcuts has the old gesture written out
/// in cockpit.json, because SaveAsync persists the whole set; without this migration they would keep Ctrl+B
/// forever. Any other gesture is left alone — but the match is on the value, so a Ctrl+B the operator chose
/// deliberately is taken as well, on every load. That is pinned below rather than left to be discovered.
/// </summary>
public class ShortcutSettingsStoreZoomMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public ShortcutSettingsStoreZoomMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task LoadAsync_SavedCtrlBZoom_MigratesToTheNewDefault()
    {
        await File.WriteAllTextAsync(_configFilePath, """
            {
              "Shortcuts": { "Gestures": { "ToggleZoom": "Ctrl+B" } }
            }
            """);
        var store = new ShortcutSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        Assert.Equal("Ctrl+Shift+M", settings.GestureFor(ShortcutAction.ToggleZoom));
    }

    [Fact]
    public async Task LoadAsync_AfterTheMigrationWasSavedBack_ChangesNothingFurther()
    {
        await File.WriteAllTextAsync(_configFilePath, """
            {
              "Shortcuts": { "Gestures": { "ToggleZoom": "Ctrl+B" } }
            }
            """);
        var store = new ShortcutSettingsStore(_configFilePath);

        await store.SaveAsync(await store.LoadAsync());
        var reloaded = await store.LoadAsync();

        Assert.Equal("Ctrl+Shift+M", reloaded.GestureFor(ShortcutAction.ToggleZoom));
    }

    [Fact]
    public async Task LoadAsync_ADeliberatelyDifferentZoomGesture_IsLeftAlone()
    {
        await File.WriteAllTextAsync(_configFilePath, """
            {
              "Shortcuts": { "Gestures": { "ToggleZoom": "Ctrl+J" } }
            }
            """);
        var store = new ShortcutSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        Assert.Equal("Ctrl+J", settings.GestureFor(ShortcutAction.ToggleZoom));
    }

    [Fact]
    public async Task LoadAsync_AConfigThatNeverSavedShortcuts_TakesTheNewDefault()
    {
        var store = new ShortcutSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        Assert.Equal("Ctrl+Shift+M", settings.GestureFor(ShortcutAction.ToggleZoom));
    }

    [Fact]
    public async Task LoadAsync_AZoomTheOperatorPutBackOnCtrlB_IsTakenAgain()
    {
        // The migration cannot tell "this is the old default" from "I chose this", so it takes both. Set up the
        // way the operator would: migrate first, then rebind to Ctrl+B through the store and restart.
        var store = new ShortcutSettingsStore(_configFilePath);
        var migrated = await store.LoadAsync();
        await store.SaveAsync(migrated.With(ShortcutAction.ToggleZoom, "Ctrl+B"));

        var afterRestart = await new ShortcutSettingsStore(_configFilePath).LoadAsync();

        Assert.Equal("Ctrl+Shift+M", afterRestart.GestureFor(ShortcutAction.ToggleZoom));
    }

    [Fact]
    public async Task LoadAsync_ACtrlBBoundToAnotherAction_IsNotSweptUpByTheZoomMigration()
    {
        await File.WriteAllTextAsync(_configFilePath, """
            {
              "Shortcuts": { "Gestures": { "About": "Ctrl+B" } }
            }
            """);
        var store = new ShortcutSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        Assert.Equal("Ctrl+B", settings.GestureFor(ShortcutAction.About));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
