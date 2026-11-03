using Cockpit.Plugin.Docker.Security;
using Cockpit.Plugin.Docker.Settings;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using NSubstitute;

namespace Cockpit.Plugin.Docker.Tests;

public sealed class DockerAccessGateTests
{
    private const string Session = "pane-1";

    private static (DockerAccessGate gate, List<ConsentRequest> asked) _Gate(ConsentOutcome outcome) =>
        _GateWithSettings(outcome, new DockerSettings(new FakePluginStorage()));

    // The fake host mirrors ConsentService's own rule (AC-1348, exercised for real in ConsentServiceTests): a
    // request carrying PreApprovedBy comes back Approved+Bypassed without ever needing an operator answer.
    private static (DockerAccessGate gate, List<ConsentRequest> asked) _GateWithSettings(ConsentOutcome outcome, DockerSettings settings)
    {
        var asked = new List<ConsentRequest>();
        var host = Substitute.For<ICockpitHost>();
        host.RequestConsentAsync(Arg.Do<ConsentRequest>(asked.Add))
            .Returns(info => Task.FromResult(_Decide(info.Arg<ConsentRequest>(), outcome)));
        return (new DockerAccessGate(host, settings), asked);
    }

    private static ConsentDecision _Decide(ConsentRequest request, ConsentOutcome outcome) =>
        request.PreApprovedBy is not null
            ? new ConsentDecision(ConsentOutcome.Approved, Bypassed: true)
            : new ConsentDecision(outcome);

    [Fact]
    public async Task Action_NeutralizesNonWhitespaceControlChars()
    {
        var (gate, asked) = _Gate(ConsentOutcome.Approved);

        var escape = ((char)0x1b).ToString();

        // A raw ANSI escape (a non-whitespace control char) must not survive into the consent body.
        await gate.AuthorizeMutationAsync("stop_container", $"stop {escape}[2Jcontainer", Session);

        // Ordinal is required here: the default culture-aware string.Contains treats a control
        // character as a zero-weight collation element, so it "matches" trivially at every
        // position — FluentAssertions' string assertions are ordinal by default, xunit's are not.
        Assert.DoesNotContain(escape, asked[1].Action, StringComparison.Ordinal);
    }

    // AC-1062, criterion 3 (mirrors ClusterAccessGate): the multi-line ingress still holds the AC-92 invariant — a
    // detail line carrying a raw newline of its own comes out escaped on its own line, not as a second line.
    [Fact]
    public async Task Mutation_ADetailLineWithAnEmbeddedNewline_ComesOutAsOneEscapedLine_NotTwoLines()
    {
        var (gate, asked) = _Gate(ConsentOutcome.Approved);

        await gate.AuthorizeMutationAsync("remove_container", "remove container \"web\"", Session, detailLines: ["exec: sh -c echo hi #harmless\nrm -rf /data"]);

        Assert.Equal(2, asked[1].Action.Split('\n').Length);
        Assert.Contains("#harmless\\nrm -rf /data", asked[1].Action, StringComparison.Ordinal);
    }

    // AC-1348: the consent mode, stored per daemon endpoint, pre-approves a call by routing PreApprovedBy to the
    // host instead of asking. Tegenproef rows: a table-classified change under ReadFree, and an unlisted tool
    // (AC-1347's hard rule — absent from the table counts as a change) both still ask.
    public static IEnumerable<object[]> ConsentModeCases()
    {
        yield return
        [
            "AC1: ReadFree pre-approves a read-classified tool",
            (Action<DockerSettings>)(s => s.ConsentMode = DockerConsentMode.ReadFree),
            (Func<DockerAccessGate, Task<GateResult>>)(gate => gate.AuthorizeConnectionAsync("list_containers", "list containers", Session)),
            1,
            true,
        ];
        yield return
        [
            "AC1 tegenproef: ReadFree still asks for a change-classified tool",
            (Action<DockerSettings>)(s => s.ConsentMode = DockerConsentMode.ReadFree),
            (Func<DockerAccessGate, Task<GateResult>>)(gate => gate.AuthorizeMutationAsync("stop_container", "stop container", Session)),
            2,
            false,
        ];
        yield return
        [
            "AC2: an unlisted tool counts as a change and still asks under ReadFree",
            (Action<DockerSettings>)(s => s.ConsentMode = DockerConsentMode.ReadFree),
            (Func<DockerAccessGate, Task<GateResult>>)(gate => gate.AuthorizeConnectionAsync("some_future_tool", "do something new", Session)),
            1,
            false,
        ];
        yield return
        [
            "AC3: AllFree pre-approves a change-classified tool too",
            (Action<DockerSettings>)(s => s.ConsentMode = DockerConsentMode.AllFree),
            (Func<DockerAccessGate, Task<GateResult>>)(gate => gate.AuthorizeMutationAsync("stop_container", "stop container", Session)),
            2,
            true,
        ];
        yield return
        [
            "AC4: an endpoint change after choosing ReadFree falls back to AlwaysAsk",
            (Action<DockerSettings>)(s =>
            {
                s.ConsentMode = DockerConsentMode.ReadFree;
                s.DaemonEndpoint = "tcp://otherhost:2375";
            }),
            (Func<DockerAccessGate, Task<GateResult>>)(gate => gate.AuthorizeConnectionAsync("list_containers", "list containers", Session)),
            1,
            false,
        ];
    }

    // `configure`/`act` arrive as `object` (boxed `Action<DockerSettings>`/`Func<DockerAccessGate, Task<GateResult>>`)
    // rather than declared as such: a public [Theory] method cannot itself declare a parameter of a type built
    // from the internal DockerSettings/DockerAccessGate/GateResult (CS0051) — the cast below is what recovers them.
    [Theory]
    [MemberData(nameof(ConsentModeCases))]
    public async Task ConsentMode_GatesEachCallByToolClassification(
        string scenario, object configure, object act, int expectedHostCalls, bool expectPreApproved)
    {
        var configureSettings = (Action<DockerSettings>)configure;
        var callGate = (Func<DockerAccessGate, Task<GateResult>>)act;
        var settings = new DockerSettings(new FakePluginStorage());
        configureSettings(settings);
        var (gate, asked) = _GateWithSettings(ConsentOutcome.Approved, settings);

        var result = await callGate(gate);

        Assert.True(result.IsAllowed, scenario);
        Assert.Equal(expectedHostCalls, asked.Count);
        Assert.Equal(expectPreApproved, asked[^1].PreApprovedBy is not null);
        Assert.Equal(expectPreApproved, result.BypassNote is not null);
    }
}
