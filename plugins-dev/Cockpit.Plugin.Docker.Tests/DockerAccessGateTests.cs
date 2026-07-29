using Cockpit.Plugin.Docker.Security;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
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

        Assert.True(result.IsAllowed);
        Assert.Single(asked);
        Assert.Equal(ConsentRisk.LowRisk, asked[0].Risk);
        Assert.True(asked[0].AllowRemember);
        Assert.Equal("docker.connect:local", asked[0].Scope);
        Assert.Equal(Session, asked[0].Source.PaneId);
    }

    [Fact]
    public async Task Mutation_AsksConnectionThenAlwaysDangerous_NeverRemembered()
    {
        var (gate, asked) = _Gate(ConsentOutcome.Approved);

        var result = await gate.AuthorizeMutationAsync("remove container \"web\"", Session);

        Assert.True(result.IsAllowed);
        Assert.Equal(2, System.Linq.Enumerable.Count(asked));
        Assert.Equal(ConsentRisk.LowRisk, asked[0].Risk);
        Assert.Equal(ConsentRisk.Dangerous, asked[1].Risk);
        Assert.False(asked[1].AllowRemember);
        Assert.Equal("docker.mutate:local", asked[1].Scope);
    }

    [Fact]
    public async Task Danger_WhenCapabilityOff_IsBlockedWithASettingsHint_NoPrompt()
    {
        var (gate, asked) = _Gate(ConsentOutcome.Approved);

        var result = await gate.AuthorizeDangerAsync(DangerCapability.Exec, enabled: false, "exec in \"web\"", Session);

        Assert.False(result.IsAllowed);
        Assert.Contains("settings", result.DeniedReason);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task Danger_WhenCapabilityOn_AsksConnectionThenDangerous()
    {
        var (gate, asked) = _Gate(ConsentOutcome.Approved);

        var result = await gate.AuthorizeDangerAsync(DangerCapability.Exec, enabled: true, "exec in \"web\": /bin/sh -c ls", Session);

        Assert.True(result.IsAllowed);
        Assert.Equal(2, System.Linq.Enumerable.Count(asked));
        Assert.Equal(ConsentRisk.Dangerous, asked[1].Risk);
        Assert.False(asked[1].AllowRemember);
        Assert.Equal("docker.exec:local", asked[1].Scope);
    }

    [Fact]
    public async Task Action_IsFlattenedToASingleLine_SoAnAgentCannotSmuggleExtraLines()
    {
        var (gate, asked) = _Gate(ConsentOutcome.Approved);

        await gate.AuthorizeMutationAsync("remove\ncontainer\n\"web\"", Session);

        // Newlines are escaped VISIBLY (as the two literal chars \n) so the operator sees the command is multi-line —
        // an agent cannot disguise a second line as commented-out — while the consent body stays one physical line.
        Assert.Equal("remove\\ncontainer\\n\"web\"", asked[1].Action);
        Assert.DoesNotContain("\n", asked[1].Action);
    }

    [Fact]
    public async Task Action_NeutralizesNonWhitespaceControlChars()
    {
        var (gate, asked) = _Gate(ConsentOutcome.Approved);

        var escape = ((char)0x1b).ToString();

        // A raw ANSI escape (a non-whitespace control char) must not survive into the consent body.
        await gate.AuthorizeMutationAsync($"stop {escape}[2Jcontainer", Session);

        // Ordinal is required here: the default culture-aware string.Contains treats a control
        // character as a zero-weight collation element, so it "matches" trivially at every
        // position — FluentAssertions' string assertions are ordinal by default, xunit's are not.
        Assert.DoesNotContain(escape, asked[1].Action, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenOperatorDeclines_ReturnsDenyWithReason()
    {
        var (gate, _) = _Gate(ConsentOutcome.Denied);

        var result = await gate.AuthorizeConnectionAsync("list containers", Session);

        Assert.False(result.IsAllowed);
        Assert.Contains("did not approve", result.DeniedReason);
    }
}
