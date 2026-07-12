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
    public void Default_BindsNewSessionToCtrlN()
        => ShortcutSettings.Default.GestureFor(ShortcutAction.NewSession).Should().Be("Ctrl+N");

    [Fact]
    public void With_RebindsOneActionAndLeavesOthers()
    {
        var settings = ShortcutSettings.Default.With(ShortcutAction.Options, "Ctrl+Shift+O");

        settings.GestureFor(ShortcutAction.Options).Should().Be("Ctrl+Shift+O");
        settings.GestureFor(ShortcutAction.NewSession).Should().Be("Ctrl+N");
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
        var settings = new ShortcutSettings(new Dictionary<ShortcutAction, string>(), new Dictionary<string, string>());

        settings.GestureFor(ShortcutAction.PluginStore).Should().Be(ShortcutCatalog.DefaultGesture(ShortcutAction.PluginStore));
    }

    [Fact]
    public void WithPlugin_OverridesAPluginShortcutGesture_AndFallsBackToTheDefaultOtherwise()
    {
        var settings = ShortcutSettings.Default.WithPlugin("youtrack.open", "Ctrl+Y");

        settings.GestureForPlugin("youtrack.open", "Shift+Y").Should().Be("Ctrl+Y");
        settings.GestureForPlugin("other.id", "Shift+Z").Should().Be("Shift+Z");
    }

    [Fact]
    public void Catalog_CoversEveryAction()
    {
        var covered = ShortcutCatalog.All.Select(descriptor => descriptor.Action);
        covered.Should().BeEquivalentTo(Enum.GetValues<ShortcutAction>());
    }
}
