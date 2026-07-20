using System.Text.Json.Nodes;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Terminal;
using Cockpit.Plugins.Abstractions.Consent;
using FluentAssertions;
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
        registry.PaneOpened("term-1", "zsh-5");

        var json = JsonNode.Parse(await tools.ReadTerminal(Session, "zsh-5"));

        json!["ok"]!.GetValue<bool>().Should().BeTrue();
        asked.Should().ContainSingle();
        asked[0].Risk.Should().Be(ConsentRisk.Dangerous, "driving a terminal is never remembered");
        asked[0].Source.PaneId.Should().Be("term-1", "the prompt appears on the pane being taken over");

        // Nothing before the coupling; output after it comes back on the next read.
        registry.CaptureOutput("term-1", "build finished\n");
        var second = JsonNode.Parse(await tools.ReadTerminal(Session, "zsh-5"));
        second!["output"]!.GetValue<string>().Should().Be("build finished\n");
        asked.Should().ContainSingle("an already-coupled pane is not re-prompted");
    }

    [Fact]
    public async Task ReadTerminal_StripsAnsiEscapes_ReturningPlainText()
    {
        // AC-34: the pane captures raw pty bytes with colour codes; read_terminal returns readable text.
        var esc = ((char)0x1b).ToString();
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        registry.PaneOpened("term-1", "zsh-5");
        await tools.ReadTerminal(Session, "zsh-5");                 // couple
        registry.CaptureOutput("term-1", $"{esc}[32mok{esc}[0m done\n");

        var json = JsonNode.Parse(await tools.ReadTerminal(Session, "zsh-5"));

        json!["output"]!.GetValue<string>().Should().Be("ok done\n");
    }

    [Fact]
    public async Task ReadTerminal_WhenDenied_ReturnsError_AndDoesNotCouple()
    {
        var (tools, registry, _, _) = _Build(ConsentOutcome.Denied);
        registry.PaneOpened("term-1", "zsh-5");

        var json = JsonNode.Parse(await tools.ReadTerminal(Session, "zsh-5"));

        json!["ok"]!.GetValue<bool>().Should().BeFalse();
        json["error"]!.GetValue<string>().Should().Contain("not approved");
        registry.IsCoupled("term-1").Should().BeFalse();
    }

    [Fact]
    public async Task ReadTerminal_UnknownPane_ReturnsError_WithoutAsking()
    {
        var (tools, _, _, asked) = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.ReadTerminal(Session, "ghost"));

        json!["ok"]!.GetValue<bool>().Should().BeFalse();
        json["error"]!.GetValue<string>().Should().Contain("No such terminal");
        asked.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadTerminal_WhenPaneCoupledToAnotherAgent_IsRefused_WithoutAsking()
    {
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        registry.PaneOpened("term-1", "zsh-5");
        registry.Couple("other-agent", "term-1");

        var json = JsonNode.Parse(await tools.ReadTerminal(Session, "zsh-5"));

        json!["ok"]!.GetValue<bool>().Should().BeFalse();
        json["error"]!.GetValue<string>().Should().Contain("another agent");
        asked.Should().BeEmpty("exclusivity is checked before the operator is bothered");
    }

    [Fact]
    public async Task ReadTerminal_WithNoConsentBroker_FailsClosed()
    {
        var registry = new TerminalAccessRegistry();
        registry.PaneOpened("term-1", "zsh-5");
        var tools = new TerminalMcpTools(registry, consent: null);

        var json = JsonNode.Parse(await tools.ReadTerminal(Session, "zsh-5"));

        json!["ok"]!.GetValue<bool>().Should().BeFalse();
        registry.IsCoupled("term-1").Should().BeFalse();
    }

    [Fact]
    public async Task SendTerminal_FirstTime_AsksConsent_ThenWritesToPty_WithEnterWhenSubmit()
    {
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        var written = new List<byte[]>();
        registry.PaneOpened("term-1", "zsh-5");
        registry.RegisterInput("term-1", bytes => written.Add(bytes.ToArray()));

        var json = JsonNode.Parse(await tools.SendTerminal(Session, "zsh-5", "echo hi", submit: true));

        json!["ok"]!.GetValue<bool>().Should().BeTrue();
        asked.Should().ContainSingle("touching a pane asks once, for read and drive together");
        System.Text.Encoding.UTF8.GetString(written.Should().ContainSingle().Subject).Should().Be("echo hi\r");
    }

    [Fact]
    public async Task SendTerminal_WhenDenied_DoesNotWrite()
    {
        var (tools, registry, _, _) = _Build(ConsentOutcome.Denied);
        var written = new List<byte[]>();
        registry.PaneOpened("term-1", "zsh-5");
        registry.RegisterInput("term-1", bytes => written.Add(bytes.ToArray()));

        var json = JsonNode.Parse(await tools.SendTerminal(Session, "zsh-5", "rm -rf /"));

        json!["ok"]!.GetValue<bool>().Should().BeFalse();
        written.Should().BeEmpty();
        registry.IsCoupled("term-1").Should().BeFalse();
    }

    [Fact]
    public void ListTerminals_ReturnsOpenPanes_WithCouplingFlag()
    {
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        registry.PaneOpened("term-1", "zsh-5");
        registry.Couple(Session, "term-1");
        registry.PaneOpened("term-2", "bash-2");

        var json = JsonNode.Parse(tools.ListTerminals(Session));

        json!["ok"]!.GetValue<bool>().Should().BeTrue();
        var names = json["terminals"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        names.Should().BeEquivalentTo("zsh-5", "bash-2");
        var coupled = json["terminals"]!.AsArray().First(t => t!["name"]!.GetValue<string>() == "zsh-5");
        coupled!["coupled"]!.GetValue<bool>().Should().BeTrue();
    }
}
