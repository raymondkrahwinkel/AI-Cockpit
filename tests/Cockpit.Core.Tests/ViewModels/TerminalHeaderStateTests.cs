using Cockpit.App.ViewModels;
using FluentAssertions;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// A plain terminal pane (#AC-25) drives the same shared header as an agent session, so the terminal treatment
/// has to hold on the shared bar: no usage pill and no plugin header host (a shell feeds neither), the "TTY" kind
/// chip, and the shell name shown once — in the title, not a second on-bar label (#AC-29). These lock that in so a
/// later header change cannot quietly bring the SDK chrome back onto a terminal. Mirrors the DesignTerminal preview
/// the Screenshotter's 'terminal' scene renders.
/// </summary>
public class TerminalHeaderStateTests
{
    [Fact]
    public void ATerminal_HasNoUsagePill()
    {
        // A shell has no context/rate feed, so the ctx pill (and its 5h/wk flyout) must not show.
        TtyViewModel.DesignTerminal().HasUsagePill.Should().BeFalse();
    }

    [Fact]
    public void ATerminal_HidesPluginHeaderItems()
    {
        // A plain shell is not an agent session, so a plugin session indicator has nothing to say about it.
        TtyViewModel.DesignTerminal().ShowPluginHeaderItems.Should().BeFalse();
    }

    [Fact]
    public void ATerminal_KeepsTheTtyKindChip()
    {
        TtyViewModel.DesignTerminal().KindLabel.Should().Be("TTY");
    }

    [Fact]
    public void ATerminal_ShowsTheShellNameInTheTitleNotADuplicateLabel()
    {
        var vm = TtyViewModel.DesignTerminal();

        // The shell name is the title; ActiveProfileLabel carries it only for the cwd tooltip, not a second
        // visible label — the double-shell-name AC-29 flagged before the AC-37 header consolidation.
        vm.Title.Should().Contain(vm.ActiveProfileLabel);
        vm.IsTerminal.Should().BeTrue();
    }
}
