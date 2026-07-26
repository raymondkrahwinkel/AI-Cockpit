using Cockpit.Core.Abstractions.Terminal;
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
        registry.PaneOpened("pane-1", "zsh-5", plainShell: true);

        // Output printed before the agent coupled — an earlier `cat .env`, say — must not be captured.
        registry.CaptureOutput("pane-1", "SECRET=hunter2\n");
        registry.Couple("session-a", "pane-1", TerminalCouplingMode.Drive);
        registry.CaptureOutput("pane-1", "hello from after the coupling\n");

        var read = registry.ReadCoupled("session-a", "pane-1")!.Text;
        read.Should().Be("hello from after the coupling\n");
        read.Should().NotContain("SECRET");
    }

    [Fact]
    public void Couple_IsExclusive_ASecondAgentIsRefused()
    {
        var registry = new TerminalAccessRegistry();
        registry.PaneOpened("pane-1", "zsh-5", plainShell: true);
        registry.Couple("session-a", "pane-1", TerminalCouplingMode.Drive);

        registry.IsCoupledByAnother("session-b", "pane-1").Should().BeTrue();
        registry.CouplingOf("session-b", "pane-1").Should().BeNull();
        var act = () => registry.Couple("session-b", "pane-1", TerminalCouplingMode.Drive);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Couple_BySameSession_IsIdempotent_AndKeepsTheCapture()
    {
        var registry = new TerminalAccessRegistry();
        registry.PaneOpened("pane-1", "zsh-5", plainShell: true);
        registry.Couple("session-a", "pane-1", TerminalCouplingMode.Drive);
        registry.CaptureOutput("pane-1", "line one\n");

        registry.Couple("session-a", "pane-1", TerminalCouplingMode.Drive); // re-couple must not reset the buffer

        registry.ReadCoupled("session-a", "pane-1")!.Text.Should().Be("line one\n");
    }

    [Fact]
    public void PaneClosed_DecouplesAutomatically()
    {
        var registry = new TerminalAccessRegistry();
        registry.PaneOpened("pane-1", "zsh-5", plainShell: true);
        registry.Couple("session-a", "pane-1", TerminalCouplingMode.Drive);

        registry.PaneClosed("pane-1");

        registry.IsCoupled("pane-1").Should().BeFalse();
        registry.ReadCoupled("session-a", "pane-1").Should().BeNull();
    }

    [Fact]
    public void SessionEnded_DecouplesEveryPaneThatSessionHeld()
    {
        var registry = new TerminalAccessRegistry();
        registry.PaneOpened("pane-1", "zsh-5", plainShell: true);
        registry.PaneOpened("pane-2", "bash-2", plainShell: true);
        registry.Couple("session-a", "pane-1", TerminalCouplingMode.Drive);
        registry.Couple("session-a", "pane-2", TerminalCouplingMode.Drive);

        registry.SessionEnded("session-a");

        registry.IsCoupled("pane-1").Should().BeFalse();
        registry.IsCoupled("pane-2").Should().BeFalse();
    }

    [Fact]
    public void Resolve_MatchesByIdOrByOperatorFacingName()
    {
        var registry = new TerminalAccessRegistry();
        registry.PaneOpened("pane-1", "zsh-5", plainShell: true);

        registry.Resolve("pane-1")!.Name.Should().Be("zsh-5");
        registry.Resolve("zsh-5")!.PaneId.Should().Be("pane-1");
        registry.Resolve("nope").Should().BeNull();
    }

    [Fact]
    public void ListPanes_LeavesOutAgentSessionPanes_SoOnlyPlainShellsAreOffered()
    {
        var registry = new TerminalAccessRegistry();
        registry.PaneOpened("pane-1", "zsh-5", plainShell: true);
        registry.PaneOpened("pane-2", "work-6", plainShell: false); // another agent's session

        registry.ListPanes("session-a").Select(pane => pane.Name).Should().Equal("zsh-5");
    }

    [Fact]
    public void Resolve_RefusesAnAgentSessionPane_EvenWhenItsIdOrNameIsKnown()
    {
        // Leaving a pane out of the list is only a gate if naming it directly fails too — otherwise an agent that
        // learned the id or the session name from anywhere else could still couple to it.
        var registry = new TerminalAccessRegistry();
        registry.PaneOpened("pane-2", "work-6", plainShell: false);

        registry.Resolve("pane-2").Should().BeNull();
        registry.Resolve("work-6").Should().BeNull();
    }

    [Fact]
    public void SendInput_OnAWatchingCoupling_IsRefused_SoApprovalToReadIsNotApprovalToType()
    {
        var registry = new TerminalAccessRegistry();
        var written = new List<byte[]>();
        registry.PaneOpened("pane-1", "zsh-5", plainShell: true);
        registry.RegisterInput("pane-1", bytes => written.Add(bytes.ToArray()));
        registry.Couple("session-a", "pane-1", TerminalCouplingMode.Watch);

        registry.SendInput("session-a", "pane-1", "rm -rf /\r"u8.ToArray()).Should().BeFalse();
        written.Should().BeEmpty();

        // Reading is what it was granted, and it still works.
        registry.CaptureOutput("pane-1", "build finished\n");
        registry.ReadCoupled("session-a", "pane-1")!.Text.Should().Be("build finished\n");
    }

    [Fact]
    public void ReadCoupled_FromAnEarlierMark_StillReturnsWhatArrivedSince_EvenAfterTheCapDroppedOlderOutput()
    {
        // The offset counts everything ever captured, not a position in the buffer. Treating it as a position breaks
        // exactly when it matters: a chatty pane pushes the buffer past its cap, the old position now points past the
        // command's own output, and a long-running command comes back with nothing.
        var registry = new TerminalAccessRegistry();
        registry.PaneOpened("pane-1", "zsh-5", plainShell: true);
        registry.Couple("session-a", "pane-1", TerminalCouplingMode.Drive);
        registry.CaptureOutput("pane-1", new string('x', 300 * 1024));   // fills and overruns the 256 KB cap

        var mark = registry.ShellStateOf("session-a", "pane-1")!.CapturedSoFar;
        registry.CaptureOutput("pane-1", "the output I asked for\n");

        registry.ReadCoupled("session-a", "pane-1", mark)!.Text.Should().Be("the output I asked for\n");
    }

    [Fact]
    public void Couple_WidensFromWatchToDrive_KeepingTheCapture_AndAnnouncesTheChange()
    {
        var registry = new TerminalAccessRegistry();
        var changes = new List<TerminalCouplingChange>();
        var written = new List<byte[]>();
        registry.CouplingChanged += changes.Add;
        registry.PaneOpened("pane-1", "zsh-5", plainShell: true);
        registry.RegisterInput("pane-1", bytes => written.Add(bytes.ToArray()));
        registry.Couple("session-a", "pane-1", TerminalCouplingMode.Watch);
        registry.CaptureOutput("pane-1", "read while watching\n");

        registry.Couple("session-a", "pane-1", TerminalCouplingMode.Drive);

        registry.CouplingOf("session-a", "pane-1").Should().Be(TerminalCouplingMode.Drive);
        registry.ReadCoupled("session-a", "pane-1")!.Text.Should().Be("read while watching\n", "widening must not cost the agent what it was already reading");
        registry.SendInput("session-a", "pane-1", "ls\r"u8.ToArray()).Should().BeTrue();
        changes.Select(change => change.Coupling).Should().Equal(TerminalCouplingMode.Watch, TerminalCouplingMode.Drive);
    }

    [Fact]
    public void Couple_AskingForWatchWhileDriving_DoesNotNarrowTheCoupling()
    {
        // Consent is not withdrawn by asking for less: a read after a send must not silently drop the keyboard.
        var registry = new TerminalAccessRegistry();
        var changes = new List<TerminalCouplingChange>();
        registry.PaneOpened("pane-1", "zsh-5", plainShell: true);
        registry.Couple("session-a", "pane-1", TerminalCouplingMode.Drive);
        registry.CouplingChanged += changes.Add;

        registry.Couple("session-a", "pane-1", TerminalCouplingMode.Watch);

        registry.CouplingOf("session-a", "pane-1").Should().Be(TerminalCouplingMode.Drive);
        changes.Should().BeEmpty("nothing changed, so the pane has nothing to redraw");
    }

    [Fact]
    public void Disconnect_OnAWatchingCoupling_DoesNotInterrupt_SoItCannotKillTheOperatorsOwnCommand()
    {
        var registry = new TerminalAccessRegistry();
        var written = new List<byte[]>();
        registry.PaneOpened("pane-1", "zsh-5", plainShell: true);
        registry.RegisterInput("pane-1", bytes => written.Add(bytes.ToArray()));
        registry.Couple("session-a", "pane-1", TerminalCouplingMode.Watch);

        registry.Disconnect("pane-1");

        written.Should().BeEmpty("a watching agent never typed, so there is nothing of its doing to interrupt");
        registry.IsCoupled("pane-1").Should().BeFalse();
    }

    [Fact]
    public void Couple_RefusesAPaneThatIsNotAnOpenPlainShell_SoTheRuleDoesNotRestOnEveryCallerUsingResolve()
    {
        // Reading and typing both need a coupling, so refusing it here is what makes the plain-shell rule hold even
        // for a caller that never went through Resolve — an unknown pane id included.
        var registry = new TerminalAccessRegistry();
        registry.PaneOpened("pane-2", "work-6", plainShell: false);

        var agentPane = () => registry.Couple("session-a", "pane-2", TerminalCouplingMode.Drive);
        agentPane.Should().Throw<InvalidOperationException>();

        var unknownPane = () => registry.Couple("session-a", "never-registered", TerminalCouplingMode.Drive);
        unknownPane.Should().Throw<InvalidOperationException>();

        registry.ReadCoupled("session-a", "pane-2").Should().BeNull();
        registry.SendInput("session-a", "pane-2", new byte[] { 1 }).Should().BeFalse();
    }

    [Fact]
    public void Resolve_ByName_SkipsAnAgentSessionPaneAndFindsThePlainShellBehindIt()
    {
        // Same operator-facing name on both: the name lookup must not stop at the agent pane and report "no such pane".
        var registry = new TerminalAccessRegistry();
        registry.PaneOpened("pane-2", "work-6", plainShell: false);
        registry.PaneOpened("pane-3", "work-6", plainShell: true);

        registry.Resolve("work-6")!.PaneId.Should().Be("pane-3");
    }

    [Fact]
    public void SendInput_WhenCoupled_WritesThroughTheRegisteredSink_ButNotWhenNotCoupled()
    {
        var registry = new TerminalAccessRegistry();
        var written = new List<byte[]>();
        registry.PaneOpened("pane-1", "zsh-5", plainShell: true);
        registry.RegisterInput("pane-1", bytes => written.Add(bytes.ToArray()));

        // Not coupled yet: a send must not reach the pty.
        registry.SendInput("session-a", "pane-1", new byte[] { 1 }).Should().BeFalse();
        written.Should().BeEmpty();

        registry.Couple("session-a", "pane-1", TerminalCouplingMode.Drive);
        registry.SendInput("session-a", "pane-1", "ls\r"u8.ToArray()).Should().BeTrue();
        registry.SendInput("session-b", "pane-1", new byte[] { 9 }).Should().BeFalse("only the coupled session can type");

        written.Should().ContainSingle();
        System.Text.Encoding.UTF8.GetString(written[0]).Should().Be("ls\r");
    }

    [Fact]
    public void Disconnect_SendsInterrupt_ThenDecouples_AndAnnounces()
    {
        var registry = new TerminalAccessRegistry();
        var written = new List<byte[]>();
        var changes = new List<TerminalCouplingChange>();
        registry.CouplingChanged += changes.Add;
        registry.PaneOpened("pane-1", "zsh-5", plainShell: true);
        registry.RegisterInput("pane-1", bytes => written.Add(bytes.ToArray()));
        registry.Couple("session-a", "pane-1", TerminalCouplingMode.Drive);

        registry.Disconnect("pane-1");

        written.Should().ContainSingle("a Ctrl-C interrupts a running command");
        written[0].Should().Equal(new byte[] { 0x03 });
        registry.IsCoupled("pane-1").Should().BeFalse();
        changes.Should().HaveCount(2);
        changes[0].Coupling.Should().Be(TerminalCouplingMode.Drive);
        changes[1].Coupling.Should().BeNull();
    }

    [Fact]
    public void CouplingChanged_FiresOnCoupleAndOnAutoDecouple()
    {
        var registry = new TerminalAccessRegistry();
        var changes = new List<TerminalCouplingChange>();
        registry.CouplingChanged += changes.Add;
        registry.PaneOpened("pane-1", "zsh-5", plainShell: true);

        registry.Couple("session-a", "pane-1", TerminalCouplingMode.Drive);
        registry.PaneClosed("pane-1");

        changes.Select(change => change.Coupling).Should().Equal(TerminalCouplingMode.Drive, null);
        changes[0].AgentSession.Should().Be("session-a");
    }
}
