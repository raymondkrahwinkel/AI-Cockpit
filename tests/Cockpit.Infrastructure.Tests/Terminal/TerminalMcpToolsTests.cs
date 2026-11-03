using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Terminal;
using Cockpit.Plugins.Abstractions.Consent;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Terminal;

/// <summary>
/// The cockpit-terminal tools (AC-34 phase 1): reading a pane is gated behind an operator Approve/Deny, coupling is
/// one-agent-per-pane, and a read returns only what was captured since the coupling.
/// </summary>
public class TerminalMcpToolsTests
{
    private const string Session = "pane-agent";

    private static (TerminalMcpTools tools, TerminalAccessRegistry registry, IConsentBroker broker, List<ConsentRequest> asked) _Build(ConsentOutcome outcome)
    {
        var registry = new TerminalAccessRegistry();
        var asked = new List<ConsentRequest>();
        var broker = Substitute.For<IConsentBroker>();
        broker.RequestConsentAsync(Arg.Do<ConsentRequest>(asked.Add), Arg.Any<CancellationToken>())
            .Returns(new ConsentDecision(outcome));
        return (new TerminalMcpTools(registry, broker), registry, broker, asked);
    }


    [Fact]
    public async Task ReadTerminal_KeysOnTheVerifiedPane_NotTheAgentSuppliedSessionId()
    {
        // Hardening (AC-89 pattern): coupling is keyed on the transport-verified pane, not the `session` the agent
        // declares. Otherwise an agent could read another session's coupled pane by naming its id (confused deputy).
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        registry.PaneOpened("term-1", "zsh-5", plainShell: true);
        registry.Couple("victim-pane", "term-1", TerminalCouplingMode.Drive);             // the pane is coupled to the victim session
        registry.CaptureOutput("term-1", "secret output\n");  // captured for the victim

        McpRequestContext.Set("attacker-pane");               // this request is verified as a different session
        try
        {
            // The attacker spoofs the victim's session id in the tool argument.
            var json = JsonNode.Parse(await tools.ReadTerminal("victim-pane", "zsh-5"));

            // Keyed on the verified "attacker-pane": the pane is coupled to another agent → refused, nothing leaks.
            Assert.False(json!["ok"]!.GetValue<bool>());
            Assert.Contains("another agent", json!["error"]!.GetValue<string>());
            Assert.Null(json!["output"]);
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }






    [Fact]
    public async Task SendTerminal_WhenDenied_DoesNotWrite()
    {
        var (tools, registry, _, _) = _Build(ConsentOutcome.Denied);
        var written = new List<byte[]>();
        registry.PaneOpened("term-1", "zsh-5", plainShell: true);
        registry.RegisterInput("term-1", bytes => written.Add(bytes.ToArray()));

        var json = JsonNode.Parse(await tools.SendTerminal(Session, "zsh-5", "rm -rf /"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Empty(written);
        Assert.False(registry.IsCoupled("term-1"));
    }


    [Fact]
    public async Task ReadTerminal_AsksOnlyToWatch_AndDoesNotGrantTyping()
    {
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        var written = new List<byte[]>();
        registry.PaneOpened("term-1", "zsh-5", plainShell: true);
        registry.RegisterInput("term-1", bytes => written.Add(bytes.ToArray()));

        await tools.ReadTerminal(Session, "zsh-5");

        Assert.Single(asked);
        Assert.Equal("terminal.watch", asked[0].Scope);
        Assert.Contains("cannot type", asked[0].Action);
        Assert.Equal(TerminalCouplingMode.Watch, registry.CouplingOf(Session, "term-1"));
        Assert.False(registry.SendInput(Session, "term-1", "ls\r"u8.ToArray()), "approving a read must not hand over the keyboard");
        Assert.Empty(written);
    }

    [Fact]
    public async Task SendTerminal_AfterOnlyWatching_AsksASecondTimeToWiden_ThenTypes()
    {
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        var written = new List<byte[]>();
        registry.PaneOpened("term-1", "zsh-5", plainShell: true);
        registry.RegisterInput("term-1", bytes => written.Add(bytes.ToArray()));
        await tools.ReadTerminal(Session, "zsh-5");           // watch only

        var json = JsonNode.Parse(await tools.SendTerminal(Session, "zsh-5", "ls", submit: true));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal(2, System.Linq.Enumerable.Count(asked));
        Assert.Equal("terminal.drive", asked[1].Scope);
        Assert.Contains("now wants to type", asked[1].Title);
        Assert.Equal("ls\r", System.Text.Encoding.UTF8.GetString(Assert.Single(written)));
    }



    private const string Osc = "\x1b]133;";
    private const string Bell = "\a";


    [Fact]
    public async Task RunInTerminal_WhenTheShellIsNotAtAPrompt_RefusesWithoutTypingAnything()
    {
        // A command is running or a full-screen program has the pane — typing a command line into vim edits a file.
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        var written = new List<byte[]>();
        registry.PaneOpened("term-1", "zsh-5", plainShell: true);
        registry.RegisterInput("term-1", bytes => written.Add(bytes.ToArray()));
        await tools.SendTerminal(Session, "zsh-5", "");            // couple with typing approved
        written.Clear();
        registry.CaptureOutput("term-1", $"{Osc}C{Bell}");          // a command started; not at a prompt

        var json = JsonNode.Parse(await tools.RunInTerminal(Session, "zsh-5", "ls"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("not sitting at a prompt", json["error"]!.GetValue<string>());
        Assert.Empty(written);
    }

    [Fact]
    public async Task RunInTerminal_AtAPrompt_RunsAndReturnsTheCommandsOwnOutputAndExitCode()
    {
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        registry.PaneOpened("term-1", "zsh-5", plainShell: true);
        registry.RegisterInput("term-1", _ => { });
        await tools.SendTerminal(Session, "zsh-5", "");
        registry.CaptureOutput("term-1", $"{Osc}B{Bell}older output that is not mine\r\n");

        var run = tools.RunInTerminal(Session, "zsh-5", "ls");
        registry.CaptureOutput("term-1", $"{Osc}C{Bell}file-a  file-b\r\n{Osc}D;0{Bell}");
        var json = JsonNode.Parse(await run);

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal(0, json["exitCode"]!.GetValue<int>());
        var output = json["output"]!.GetValue<string>();
        Assert.Contains("file-a  file-b", output);
        Assert.DoesNotContain("older output", output);
    }


    [Fact]
    public async Task RunInTerminal_DoesNotAcceptAFinishFromACommandItNeverStarted()
    {
        // The pane is shared: the operator can hit Enter in the moment between the at-prompt check and the send. A
        // bare finish counter would read their command's exit code as this command's result.
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        registry.PaneOpened("term-1", "zsh-5", plainShell: true);
        registry.RegisterInput("term-1", _ => { });
        await tools.SendTerminal(Session, "zsh-5", "");
        registry.CaptureOutput("term-1", $"{Osc}B{Bell}");

        var run = tools.RunInTerminal(Session, "zsh-5", "mine", timeoutSeconds: 2);
        registry.CaptureOutput("term-1", $"{Osc}D;3{Bell}");   // a finish with no start of ours behind it
        var json = JsonNode.Parse(await run);

        Assert.False(json!["ok"]!.GetValue<bool>(), "a finish alone does not mean the command we sent is done");
        Assert.Contains("still running", json["error"]!.GetValue<string>());
    }


    [Fact]
    public async Task WhenAnotherAgentTakesThePaneWhileTheOperatorDecides_TheRefusalIsAnErrorNotAnException()
    {
        // Consent takes as long as the operator takes, and the world moves meanwhile. This used to throw straight
        // out of the tool call instead of coming back in the shape every other refusal here uses.
        var registry = new TerminalAccessRegistry();
        registry.PaneOpened("term-1", "zsh-5", plainShell: true);
        var broker = Substitute.For<IConsentBroker>();
        broker.RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                registry.Couple("someone-else", "term-1", TerminalCouplingMode.Drive); // slipped in while we asked
                return new ConsentDecision(ConsentOutcome.Approved);
            });
        var tools = new TerminalMcpTools(registry, broker);

        var json = JsonNode.Parse(await tools.ReadTerminal(Session, "zsh-5"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("no longer available", json["error"]!.GetValue<string>());
    }




}
