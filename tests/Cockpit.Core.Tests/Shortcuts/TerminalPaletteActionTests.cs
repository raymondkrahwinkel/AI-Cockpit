using Cockpit.App.ViewModels;
using Cockpit.Core.Shortcuts;

namespace Cockpit.Core.Tests.Shortcuts;

/// <summary>
/// "New terminal" in the command palette (AC-26 residual, once terminals landed via AC-25). A plain-terminal
/// pane is opened far less often than a session, so it is unbound by default and found in the palette beside New
/// session, rather than spending a scarce Ctrl+letter the shell underneath wants.
/// </summary>
public class TerminalPaletteActionTests
{
    [Fact]
    public void NewTerminal_IsInTheCatalogAndUnboundByDefault()
    {
        var descriptor = Assert.Single(ShortcutCatalog.All, entry => entry.Action == ShortcutAction.NewTerminal);

        Assert.Equal("New terminal", descriptor.Label);
        Assert.Empty(descriptor.DefaultGesture);
    }

    [Fact]
    public void ThePalette_OffersNewTerminal()
    {
        var titles = new CockpitViewModel().BuildPaletteCommands().Select(command => command.Title).ToList();

        Assert.Contains("New terminal", titles);
    }

    /// <summary>
    /// Unbound and palette-only, it stands down over a focused terminal like the other non-navigation actions —
    /// so a key the operator later binds to it is left to the shell while a TUI has the keyboard.
    /// </summary>
    [Fact]
    public void NewTerminal_DoesNotStayActiveInATerminal()
    {
        Assert.False(ShortcutCatalog.StaysActiveInTerminal(ShortcutAction.NewTerminal));
    }
}
