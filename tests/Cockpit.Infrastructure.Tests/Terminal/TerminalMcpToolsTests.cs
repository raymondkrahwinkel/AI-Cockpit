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
    public async Task ReadTerminal_FirstTime_AsksConsent_ThenReturnsOutputCapturedSinceCoupling()
    {
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        registry.PaneOpened("term-1", "zsh-5", plainShell: true);

        var json = JsonNode.Parse(await tools.ReadTerminal(Session, "zsh-5"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Single(asked);
        Assert.Equal(ConsentRisk.Dangerous, asked[0].Risk);
        Assert.Equal("term-1", asked[0].Source.PaneId);

        // Nothing before the coupling; output after it comes back on the next read.
        registry.CaptureOutput("term-1", "build finished\n");
        var second = JsonNode.Parse(await tools.ReadTerminal(Session, "zsh-5"));
        Assert.Equal("build finished\n", second!["output"]!.GetValue<string>());
        Assert.Single(asked);
    }

    [Fact]
    public async Task ReadTerminal_StripsAnsiEscapes_ReturningPlainText()
    {
        // AC-34: the pane captures raw pty bytes with colour codes; read_terminal returns readable text.
        var esc = ((char)0x1b).ToString();
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        registry.PaneOpened("term-1", "zsh-5", plainShell: true);
        await tools.ReadTerminal(Session, "zsh-5");                 // couple
        registry.CaptureOutput("term-1", $"{esc}[32mok{esc}[0m done\n");

        var json = JsonNode.Parse(await tools.ReadTerminal(Session, "zsh-5"));

        Assert.Equal("ok done\n", json!["output"]!.GetValue<string>());
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
    public async Task ReadTerminal_WhenDenied_ReturnsError_AndDoesNotCouple()
    {
        var (tools, registry, _, _) = _Build(ConsentOutcome.Denied);
        registry.PaneOpened("term-1", "zsh-5", plainShell: true);

        var json = JsonNode.Parse(await tools.ReadTerminal(Session, "zsh-5"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("not approved", json["error"]!.GetValue<string>());
        Assert.False(registry.IsCoupled("term-1"));
    }

    [Fact]
    public async Task ReadTerminal_UnknownPane_ReturnsError_WithoutAsking()
    {
        var (tools, _, _, asked) = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.ReadTerminal(Session, "ghost"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("No such terminal", json["error"]!.GetValue<string>());
        Assert.Empty(asked);
    }

    [Fact]
    public async Task ReadTerminal_WhenPaneCoupledToAnotherAgent_IsRefused_WithoutAsking()
    {
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        registry.PaneOpened("term-1", "zsh-5", plainShell: true);
        registry.Couple("other-agent", "term-1", TerminalCouplingMode.Drive);

        var json = JsonNode.Parse(await tools.ReadTerminal(Session, "zsh-5"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("another agent", json["error"]!.GetValue<string>());
        Assert.Empty(asked);
    }

    [Fact]
    public async Task ReadTerminal_WithNoConsentBroker_FailsClosed()
    {
        var registry = new TerminalAccessRegistry();
        registry.PaneOpened("term-1", "zsh-5", plainShell: true);
        var tools = new TerminalMcpTools(registry, consent: null);

        var json = JsonNode.Parse(await tools.ReadTerminal(Session, "zsh-5"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.False(registry.IsCoupled("term-1"));
    }

    [Fact]
    public async Task SendTerminal_FirstTime_AsksConsent_ThenWritesToPty_WithEnterWhenSubmit()
    {
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        var written = new List<byte[]>();
        registry.PaneOpened("term-1", "zsh-5", plainShell: true);
        registry.RegisterInput("term-1", bytes => written.Add(bytes.ToArray()));

        var json = JsonNode.Parse(await tools.SendTerminal(Session, "zsh-5", "echo hi", submit: true));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Single(asked);
        Assert.Equal("echo hi\r", System.Text.Encoding.UTF8.GetString(Assert.Single(written)));
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
    public void ListTerminals_ReturnsOpenPanes_WithCouplingFlag()
    {
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        registry.PaneOpened("term-1", "zsh-5", plainShell: true);
        registry.Couple(Session, "term-1", TerminalCouplingMode.Drive);
        registry.PaneOpened("term-2", "bash-2", plainShell: true);

        var json = JsonNode.Parse(tools.ListTerminals(Session));

        Assert.True(json!["ok"]!.GetValue<bool>());
        var names = json["terminals"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Equivalent(new object[] { "zsh-5", "bash-2" }, names);
        var coupled = json["terminals"]!.AsArray().First(t => t!["name"]!.GetValue<string>() == "zsh-5");
        Assert.True(coupled!["coupled"]!.GetValue<bool>());
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

    [Fact]
    public async Task SendTerminal_WhenWideningIsDenied_LeavesTheReadAccessItAlreadyHad()
    {
        var registry = new TerminalAccessRegistry();
        var broker = Substitute.For<IConsentBroker>();
        var outcomes = new Queue<ConsentOutcome>([ConsentOutcome.Approved, ConsentOutcome.Denied]);
        broker.RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ConsentDecision(outcomes.Dequeue()));
        var tools = new TerminalMcpTools(registry, broker);
        registry.PaneOpened("term-1", "zsh-5", plainShell: true);
        await tools.ReadTerminal(Session, "zsh-5");           // watch approved

        var json = JsonNode.Parse(await tools.SendTerminal(Session, "zsh-5", "ls", submit: true));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("still be able to read", json["error"]!.GetValue<string>());
        Assert.Equal(TerminalCouplingMode.Watch, registry.CouplingOf(Session, "term-1"));
    }

    [Fact]
    public async Task SendTerminal_OnAFreshPane_AsksForTypingOnce_NotTwice()
    {
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        registry.PaneOpened("term-1", "zsh-5", plainShell: true);
        registry.RegisterInput("term-1", _ => { });

        await tools.SendTerminal(Session, "zsh-5", "ls", submit: true);

        Assert.Single(asked);
        Assert.Equal("terminal.drive", asked[0].Scope);
        Assert.Equal(TerminalCouplingMode.Drive, registry.CouplingOf(Session, "term-1"));
    }

    private const string Osc = "\x1b]133;";
    private const string Bell = "\a";

    [Fact]
    public async Task RunInTerminal_WithoutShellIntegration_RefusesWithoutTypingAnything()
    {
        // The whole point: no marks means no honest way to say "finished", so it must not run the command and then
        // hand back whatever happened to be on screen.
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        var written = new List<byte[]>();
        registry.PaneOpened("term-1", "zsh-5", plainShell: true);
        registry.RegisterInput("term-1", bytes => written.Add(bytes.ToArray()));

        var json = JsonNode.Parse(await tools.RunInTerminal(Session, "zsh-5", "ls"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        // Named specifically: the at-prompt guard below refuses this same input for its own reason, and asserting on
        // the shared "Nothing was run" would pass even with this guard gone.
        Assert.Contains("does not publish shell-integration marks", json["error"]!.GetValue<string>());
        Assert.Empty(written);
    }

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
    public async Task RunInTerminal_ThatDoesNotFinishInTime_SaysSo_WithoutClaimingItWasCancelled()
    {
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        var written = new List<byte[]>();
        registry.PaneOpened("term-1", "zsh-5", plainShell: true);
        registry.RegisterInput("term-1", bytes => written.Add(bytes.ToArray()));
        await tools.SendTerminal(Session, "zsh-5", "");
        written.Clear();
        registry.CaptureOutput("term-1", $"{Osc}B{Bell}");

        var json = JsonNode.Parse(await tools.RunInTerminal(Session, "zsh-5", "sleep 900", timeoutSeconds: 1));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("still running", json["error"]!.GetValue<string>());
        Assert.Contains("not cancelled", json["error"]!.GetValue<string>());
        Assert.Equal("sleep 900\r", System.Text.Encoding.UTF8.GetString(Assert.Single(written)));
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
    public async Task ReadTerminal_SaysSoWhenTheBufferCapDroppedPartOfWhatItIsReturning()
    {
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        registry.PaneOpened("term-1", "zsh-5", plainShell: true);
        await tools.ReadTerminal(Session, "zsh-5");
        registry.CaptureOutput("term-1", new string('x', 300 * 1024));

        var json = JsonNode.Parse(await tools.ReadTerminal(Session, "zsh-5"));

        Assert.True(json!["truncated"]!.GetValue<bool>(), "an agent must not read a build as clean when the errors scrolled out of reach");
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

    [Fact]
    public async Task RunInTerminal_NeedsTypingApproval_NotJustReading()
    {
        var registry = new TerminalAccessRegistry();
        var broker = Substitute.For<IConsentBroker>();
        broker.RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ConsentDecision(ConsentOutcome.Denied));
        var tools = new TerminalMcpTools(registry, broker);
        var written = new List<byte[]>();
        registry.PaneOpened("term-1", "zsh-5", plainShell: true);
        registry.RegisterInput("term-1", bytes => written.Add(bytes.ToArray()));

        var json = JsonNode.Parse(await tools.RunInTerminal(Session, "zsh-5", "ls"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Empty(written);
        Assert.Null(registry.CouplingOf(Session, "term-1"));
    }

    [Fact]
    public void ListTerminals_OmitsPanesRunningAnotherAgent()
    {
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        registry.PaneOpened("term-1", "zsh-5", plainShell: true);
        registry.PaneOpened("term-2", "work-6", plainShell: false);

        var json = JsonNode.Parse(tools.ListTerminals(Session));

        Assert.Equal(new[] { "zsh-5" }, json!["terminals"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ReadTerminal_OfAPaneRunningAnotherAgent_IsRefused_WithoutAsking()
    {
        // The operator must never be prompted to approve reading another agent's session: its pane renders that
        // session's whole transcript, and a well-meaning Approve would hand it over.
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        registry.PaneOpened("term-2", "work-6", plainShell: false);
        registry.CaptureOutput("term-2", "the other session's transcript\n");

        var json = JsonNode.Parse(await tools.ReadTerminal(Session, "work-6"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("No such terminal", json["error"]!.GetValue<string>());
        Assert.Null(json["output"]);
        Assert.Empty(asked);
        Assert.False(registry.IsCoupled("term-2"));
    }

    [Fact]
    public async Task SendTerminal_ToAPaneRunningAnotherAgent_IsRefused()
    {
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        var written = new List<byte[]>();
        registry.PaneOpened("term-2", "work-6", plainShell: false);
        registry.RegisterInput("term-2", bytes => written.Add(bytes.ToArray()));

        var json = JsonNode.Parse(await tools.SendTerminal(Session, "work-6", "/exit", submit: true));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Empty(written);
        Assert.Empty(asked);
    }
}
