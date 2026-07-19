using Cockpit.Infrastructure.Terminal;
using FluentAssertions;

namespace Cockpit.Infrastructure.Tests.Terminal;

/// <summary>
/// The coupling/read-scope rules behind the terminal-access MCP (AC-34): capture starts at the coupling (never the
/// earlier scrollback), one agent per pane, and a pane close or a session end decouples on its own.
/// </summary>
public class TerminalAccessRegistryTests
{
    [Fact]
    public void CaptureOutput_BeforeCoupling_IsNotBuffered_SoEarlierScrollbackNeverLeaks()
    {
        var registry = new TerminalAccessRegistry();
        registry.PaneOpened("pane-1", "zsh-5");

        // Output printed before the agent coupled — an earlier `cat .env`, say — must not be captured.
        registry.CaptureOutput("pane-1", "SECRET=hunter2\n");
        registry.Couple("session-a", "pane-1");
        registry.CaptureOutput("pane-1", "hello from after the coupling\n");

        var read = registry.ReadCoupled("session-a", "pane-1");
        read.Should().Be("hello from after the coupling\n");
        read.Should().NotContain("SECRET");
    }

    [Fact]
    public void Couple_IsExclusive_ASecondAgentIsRefused()
    {
        var registry = new TerminalAccessRegistry();
        registry.PaneOpened("pane-1", "zsh-5");
        registry.Couple("session-a", "pane-1");

        registry.IsCoupledByAnother("session-b", "pane-1").Should().BeTrue();
        registry.IsCoupledBy("session-b", "pane-1").Should().BeFalse();
        var act = () => registry.Couple("session-b", "pane-1");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Couple_BySameSession_IsIdempotent_AndKeepsTheCapture()
    {
        var registry = new TerminalAccessRegistry();
        registry.PaneOpened("pane-1", "zsh-5");
        registry.Couple("session-a", "pane-1");
        registry.CaptureOutput("pane-1", "line one\n");

        registry.Couple("session-a", "pane-1"); // re-couple must not reset the buffer

        registry.ReadCoupled("session-a", "pane-1").Should().Be("line one\n");
    }

    [Fact]
    public void PaneClosed_DecouplesAutomatically()
    {
        var registry = new TerminalAccessRegistry();
        registry.PaneOpened("pane-1", "zsh-5");
        registry.Couple("session-a", "pane-1");

        registry.PaneClosed("pane-1");

        registry.IsCoupled("pane-1").Should().BeFalse();
        registry.ReadCoupled("session-a", "pane-1").Should().BeNull();
    }

    [Fact]
    public void SessionEnded_DecouplesEveryPaneThatSessionHeld()
    {
        var registry = new TerminalAccessRegistry();
        registry.PaneOpened("pane-1", "zsh-5");
        registry.PaneOpened("pane-2", "bash-2");
        registry.Couple("session-a", "pane-1");
        registry.Couple("session-a", "pane-2");

        registry.SessionEnded("session-a");

        registry.IsCoupled("pane-1").Should().BeFalse();
        registry.IsCoupled("pane-2").Should().BeFalse();
    }

    [Fact]
    public void Resolve_MatchesByIdOrByOperatorFacingName()
    {
        var registry = new TerminalAccessRegistry();
        registry.PaneOpened("pane-1", "zsh-5");

        registry.Resolve("pane-1")!.Name.Should().Be("zsh-5");
        registry.Resolve("zsh-5")!.PaneId.Should().Be("pane-1");
        registry.Resolve("nope").Should().BeNull();
    }
}
