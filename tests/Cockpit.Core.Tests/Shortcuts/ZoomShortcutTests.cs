using Avalonia.Input;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Shortcuts;

namespace Cockpit.Core.Tests.Shortcuts;

/// <summary>
/// Toggle zoom has to survive a focused terminal: zooming a pane is something you reach for while driving the
/// session that is running in it, so the moment the shortcut matters is the moment the terminal owns the
/// keyboard. It gets there on its two modifiers alone — it is deliberately not in
/// <see cref="ShortcutCatalog.StaysActiveInTerminal"/>, which would hand it the terminal's keys wholesale.
/// These tests take the catalog default and the real gate; the binding in between is rebuilt here rather than
/// taken from <c>CockpitViewModel</c>, so a change to how that builder fills the flags would not show up here —
/// <c>CockpitViewModelTests.ActiveShortcuts_…</c> is what pins the builder itself.
/// </summary>
public class ZoomShortcutTests
{
    // Live over a focused terminal on its two modifiers alone, where the one-modifier Ctrl+B it replaced went to
    // the shell instead — the bug this chord exists to fix. The gesture's own value is
    // ShortcutSettingsStoreZoomMigrationTests' to pin, and its absence from the allow-list is settled by the sweep.
    [Theory]
    [InlineData(null, true)]
    [InlineData("Ctrl+B", false)]
    public void TheZoomBinding_IsLiveWithTheTerminalFocused_WhereItsOneModifierPredecessorWasNot(
        string? gesture, bool expected)
    {
        var zoom = BindingFor(ShortcutAction.ToggleZoom);
        zoom = gesture is null ? zoom : zoom with { Gesture = gesture };

        Assert.Equal(
            expected,
            ShortcutDispatchGate.IsBindingLive(zoom, KeyGesture.Parse(zoom.Gesture), ShortcutFocus.Terminal));
    }

    [Fact]
    public void NoTwoActions_ShipWithTheSameDefaultGesture()
    {
        // Compared as parsed gestures, not as strings: "Ctrl+Shift+M" and "Shift+Ctrl+M" are the same key press
        // and a string comparison would wave the second one through.
        var doubleBound = ShortcutCatalog.All
            .Where(descriptor => !string.IsNullOrWhiteSpace(descriptor.DefaultGesture))
            .GroupBy(descriptor => KeyGesture.Parse(descriptor.DefaultGesture).ToString())
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(descriptor => descriptor.Label))}");

        Assert.Empty(doubleBound);
    }

    // Mirrors how CockpitViewModel builds a binding for an app action: the command palette is the only
    // always-active one, and staying active in the terminal comes from the catalog.
    private static ShortcutBinding BindingFor(ShortcutAction action)
    {
        var descriptor = ShortcutCatalog.All.Single(entry => entry.Action == action);
        return new ShortcutBinding(
            descriptor.DefaultGesture,
            descriptor.Label,
            () => { },
            action == ShortcutAction.CommandPalette,
            ShortcutCatalog.StaysActiveInTerminal(action));
    }
}
