using Cockpit.Plugin.Docker.Security;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Plugin.Docker.Tests;

public sealed class DockerAccessGateTests
{
    private const string Session = "pane-1";

    private static (DockerAccessGate gate, List<ConsentRequest> asked) _Gate(ConsentOutcome outcome)
    {
        var asked = new List<ConsentRequest>();
        var host = Substitute.For<ICockpitHost>();
        host.RequestConsentAsync(Arg.Do<ConsentRequest>(asked.Add)).Returns(new ConsentDecision(outcome));
        return (new DockerAccessGate(host), asked);
    }

    [Fact]
    public async Task Connection_AsksOnce_LowRisk_RememberedForThePane()
    {
        var (gate, asked) = _Gate(ConsentOutcome.Approved);

        var result = await gate.AuthorizeConnectionAsync("list containers", Session);

        result.IsAllowed.Should().BeTrue();
        asked.Should().ContainSingle();
        asked[0].Risk.Should().Be(ConsentRisk.LowRisk);
        asked[0].AllowRemember.Should().BeTrue();
        asked[0].Scope.Should().Be("docker.connect:local");
        asked[0].Source.PaneId.Should().Be(Session);
    }

    [Fact]
    public async Task Mutation_AsksConnectionThenAlwaysDangerous_NeverRemembered()
    {
        var (gate, asked) = _Gate(ConsentOutcome.Approved);

        var result = await gate.AuthorizeMutationAsync("remove container \"web\"", Session);

        result.IsAllowed.Should().BeTrue();
        asked.Should().HaveCount(2);
        asked[0].Risk.Should().Be(ConsentRisk.LowRisk, "connection is authorized first");
        asked[1].Risk.Should().Be(ConsentRisk.Dangerous);
        asked[1].AllowRemember.Should().BeFalse();
        asked[1].Scope.Should().Be("docker.mutate:local");
    }

    [Fact]
    public async Task Danger_WhenCapabilityOff_IsBlockedWithASettingsHint_NoPrompt()
    {
        var (gate, asked) = _Gate(ConsentOutcome.Approved);

        var result = await gate.AuthorizeDangerAsync(DangerCapability.Exec, enabled: false, "exec in \"web\"", Session);

        result.IsAllowed.Should().BeFalse();
        result.DeniedReason.Should().Contain("settings");
        asked.Should().BeEmpty("a capability that is off is a policy block — no prompt");
    }

    [Fact]
    public async Task Danger_WhenCapabilityOn_AsksConnectionThenDangerous()
    {
        var (gate, asked) = _Gate(ConsentOutcome.Approved);

        var result = await gate.AuthorizeDangerAsync(DangerCapability.Exec, enabled: true, "exec in \"web\": /bin/sh -c ls", Session);

        result.IsAllowed.Should().BeTrue();
        asked.Should().HaveCount(2);
        asked[1].Risk.Should().Be(ConsentRisk.Dangerous);
        asked[1].AllowRemember.Should().BeFalse();
        asked[1].Scope.Should().Be("docker.exec:local");
    }

    [Fact]
    public async Task Action_IsFlattenedToASingleLine_SoAnAgentCannotSmuggleExtraLines()
    {
        var (gate, asked) = _Gate(ConsentOutcome.Approved);

        await gate.AuthorizeMutationAsync("remove\ncontainer\n\"web\"", Session);

        // Newlines are escaped VISIBLY (as the two literal chars \n) so the operator sees the command is multi-line —
        // an agent cannot disguise a second line as commented-out — while the consent body stays one physical line.
        asked[1].Action.Should().Be("remove\\ncontainer\\n\"web\"");
        asked[1].Action.Should().NotContain("\n");
    }

    [Fact]
    public async Task Action_NeutralizesNonWhitespaceControlChars()
    {
        var (gate, asked) = _Gate(ConsentOutcome.Approved);

        var escape = ((char)0x1b).ToString();

        // A raw ANSI escape (a non-whitespace control char) must not survive into the consent body.
        await gate.AuthorizeMutationAsync($"stop {escape}[2Jcontainer", Session);

        asked[1].Action.Should().NotContain(escape);
    }

    [Fact]
    public async Task WhenOperatorDeclines_ReturnsDenyWithReason()
    {
        var (gate, _) = _Gate(ConsentOutcome.Denied);

        var result = await gate.AuthorizeConnectionAsync("list containers", Session);

        result.IsAllowed.Should().BeFalse();
        result.DeniedReason.Should().Contain("did not approve");
    }
}
