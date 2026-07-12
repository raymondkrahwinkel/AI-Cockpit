using Cockpit.Core.Shortcuts;
using FluentAssertions;

namespace Cockpit.Core.Tests.Shortcuts;

/// <summary>
/// The app-action shortcut model: defaults come from the catalog, <see cref="ShortcutSettings.With"/> rebinds
/// (and unbinds on blank), and <see cref="ShortcutSettings.GestureFor"/> falls back to the catalog default for
/// an action the settings never carried.
/// </summary>
public class ShortcutSettingsTests
{
    [Fact]
    public void Default_BindsNewSessionToShiftN()
        => ShortcutSettings.Default.GestureFor(ShortcutAction.NewSession).Should().Be("Shift+N");

    [Fact]
    public void With_RebindsOneActionAndLeavesOthers()
    {
        var settings = ShortcutSettings.Default.With(ShortcutAction.Options, "Ctrl+Shift+O");

        settings.GestureFor(ShortcutAction.Options).Should().Be("Ctrl+Shift+O");
        settings.GestureFor(ShortcutAction.NewSession).Should().Be("Shift+N");
    }

    [Fact]
    public void With_BlankUnbindsTheAction()
    {
        var settings = ShortcutSettings.Default.With(ShortcutAction.NewSession, "   ");

        settings.GestureFor(ShortcutAction.NewSession).Should().BeEmpty();
    }

    [Fact]
    public void GestureFor_FallsBackToCatalogDefaultWhenUnset()
    {
        var settings = new ShortcutSettings(new Dictionary<ShortcutAction, string>());

        settings.GestureFor(ShortcutAction.PluginStore).Should().Be(ShortcutCatalog.DefaultGesture(ShortcutAction.PluginStore));
    }

    [Fact]
    public void Catalog_CoversEveryAction()
    {
        var covered = ShortcutCatalog.All.Select(descriptor => descriptor.Action);
        covered.Should().BeEquivalentTo(Enum.GetValues<ShortcutAction>());
    }
}
