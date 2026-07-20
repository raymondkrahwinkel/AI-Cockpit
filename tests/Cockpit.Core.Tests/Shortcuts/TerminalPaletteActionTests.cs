using Cockpit.App.ViewModels;
using Cockpit.Core.Shortcuts;
using FluentAssertions;

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
        var descriptor = ShortcutCatalog.All.Should().ContainSingle(entry => entry.Action == ShortcutAction.NewTerminal).Subject;

        descriptor.Label.Should().Be("New terminal");
        descriptor.DefaultGesture.Should().BeEmpty();
    }

    [Fact]
    public void ThePalette_OffersNewTerminal()
    {
        var titles = new CockpitViewModel().BuildPaletteCommands().Select(command => command.Title).ToList();

        titles.Should().Contain("New terminal");
    }

    /// <summary>
    /// Unbound and palette-only, it stands down over a focused terminal like the other non-navigation actions —
    /// so a key the operator later binds to it is left to the shell while a TUI has the keyboard.
    /// </summary>
    [Fact]
    public void NewTerminal_DoesNotStayActiveInATerminal()
    {
        ShortcutCatalog.StaysActiveInTerminal(ShortcutAction.NewTerminal).Should().BeFalse();
    }
}
