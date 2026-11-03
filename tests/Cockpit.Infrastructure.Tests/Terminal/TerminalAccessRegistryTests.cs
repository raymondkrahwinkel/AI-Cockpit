using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Infrastructure.Terminal;

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
        Assert.Equal("hello from after the coupling\n", read);
        Assert.DoesNotContain("SECRET", read);
    }








    [Fact]
    public void SendInput_OnAWatchingCoupling_IsRefused_SoApprovalToReadIsNotApprovalToType()
    {
        var registry = new TerminalAccessRegistry();
        var written = new List<byte[]>();
        registry.PaneOpened("pane-1", "zsh-5", plainShell: true);
        registry.RegisterInput("pane-1", bytes => written.Add(bytes.ToArray()));
        registry.Couple("session-a", "pane-1", TerminalCouplingMode.Watch);

        Assert.False(registry.SendInput("session-a", "pane-1", "rm -rf /\r"u8.ToArray()));
        Assert.Empty(written);

        // Reading is what it was granted, and it still works.
        registry.CaptureOutput("pane-1", "build finished\n");
        Assert.Equal("build finished\n", registry.ReadCoupled("session-a", "pane-1")!.Text);
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

        Assert.Empty(written);
        Assert.False(registry.IsCoupled("pane-1"));
    }

    [Fact]
    public void Couple_RefusesAPaneThatIsNotAnOpenPlainShell_SoTheRuleDoesNotRestOnEveryCallerUsingResolve()
    {
        // Reading and typing both need a coupling, so refusing it here is what makes the plain-shell rule hold even
        // for a caller that never went through Resolve — an unknown pane id included.
        var registry = new TerminalAccessRegistry();
        registry.PaneOpened("pane-2", "work-6", plainShell: false);

        var agentPane = () => registry.Couple("session-a", "pane-2", TerminalCouplingMode.Drive);
        Assert.Throws<InvalidOperationException>(agentPane);

        var unknownPane = () => registry.Couple("session-a", "never-registered", TerminalCouplingMode.Drive);
        Assert.Throws<InvalidOperationException>(unknownPane);

        Assert.Null(registry.ReadCoupled("session-a", "pane-2"));
        Assert.False(registry.SendInput("session-a", "pane-2", new byte[] { 1 }));
    }


    [Fact]
    public void SendInput_WhenCoupled_WritesThroughTheRegisteredSink_ButNotWhenNotCoupled()
    {
        var registry = new TerminalAccessRegistry();
        var written = new List<byte[]>();
        registry.PaneOpened("pane-1", "zsh-5", plainShell: true);
        registry.RegisterInput("pane-1", bytes => written.Add(bytes.ToArray()));

        // Not coupled yet: a send must not reach the pty.
        Assert.False(registry.SendInput("session-a", "pane-1", new byte[] { 1 }));
        Assert.Empty(written);

        registry.Couple("session-a", "pane-1", TerminalCouplingMode.Drive);
        Assert.True(registry.SendInput("session-a", "pane-1", "ls\r"u8.ToArray()));
        Assert.False(registry.SendInput("session-b", "pane-1", new byte[] { 9 }), "only the coupled session can type");

        Assert.Single(written);
        Assert.Equal("ls\r", System.Text.Encoding.UTF8.GetString(written[0]));
    }

}
