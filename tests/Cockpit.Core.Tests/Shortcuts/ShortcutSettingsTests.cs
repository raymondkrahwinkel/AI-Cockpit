using Cockpit.Core.Shortcuts;

namespace Cockpit.Core.Tests.Shortcuts;

/// <summary>
/// The app-action shortcut model: defaults come from the catalog, <see cref="ShortcutSettings.With"/> rebinds
/// (and unbinds on blank), and <see cref="ShortcutSettings.GestureFor"/> falls back to the catalog default for
/// an action the settings never carried.
/// </summary>
public class ShortcutSettingsTests
{
    [Fact]
    public void Default_BindsNewSessionToCtrlN()
        => Assert.Equal("Ctrl+N", ShortcutSettings.Default.GestureFor(ShortcutAction.NewSession));

    [Fact]
    public void With_RebindsOneActionAndLeavesOthers()
    {
        var settings = ShortcutSettings.Default.With(ShortcutAction.Options, "Ctrl+Shift+O");

        Assert.Equal("Ctrl+Shift+O", settings.GestureFor(ShortcutAction.Options));
        Assert.Equal("Ctrl+N", settings.GestureFor(ShortcutAction.NewSession));
    }

    [Fact]
    public void With_BlankUnbindsTheAction()
    {
        var settings = ShortcutSettings.Default.With(ShortcutAction.NewSession, "   ");

        Assert.Empty(settings.GestureFor(ShortcutAction.NewSession));
    }

    [Fact]
    public void GestureFor_FallsBackToCatalogDefaultWhenUnset()
    {
        var settings = new ShortcutSettings(new Dictionary<ShortcutAction, string>(), new Dictionary<string, string>());

        Assert.Equal(ShortcutCatalog.DefaultGesture(ShortcutAction.PluginStore), settings.GestureFor(ShortcutAction.PluginStore));
    }

    [Fact]
    public void WithPlugin_OverridesAPluginShortcutGesture_AndFallsBackToTheDefaultOtherwise()
    {
        var settings = ShortcutSettings.Default.WithPlugin("youtrack.open", "Ctrl+Y");

        Assert.Equal("Ctrl+Y", settings.GestureForPlugin("youtrack.open", "Shift+Y"));
        Assert.Equal("Shift+Z", settings.GestureForPlugin("other.id", "Shift+Z"));
    }

    [Fact]
    public void Catalog_CoversEveryAction()
    {
        var covered = ShortcutCatalog.All.Select(descriptor => descriptor.Action);
        Assert.Equivalent(Enum.GetValues<ShortcutAction>(), covered);
    }
}
