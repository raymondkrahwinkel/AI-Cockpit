using Cockpit.Plugin.LocalCi.Execution;
using Cockpit.Plugin.LocalCi.Gate;
using Cockpit.Plugin.LocalCi.Runtime;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.LocalCi.Tests;

public class PullRequestGateTests
{
    private const string Head = "1111111111111111111111111111111111111111";
    private const string Older = "2222222222222222222222222222222222222222";
    private static readonly string Checkout = Path.Combine(Path.GetTempPath(), "gated-checkout");
    private static readonly DateTimeOffset Noon = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeCockpitHost _host = new();
    private readonly LocalRunTracker _tracker = new();
    private readonly PullRequestGateSettings _settings;

    public PullRequestGateTests() => _settings = new PullRequestGateSettings(_host.Storage);

    [Fact]
    public async Task EveryEndingThatIsNotAPassIsRefused()
    {
        _settings.Set(Checkout, on: true);

        foreach (var outcome in Enum.GetValues<LocalRunOutcome>().Where(o => o != LocalRunOutcome.Passed))
        {
            _tracker.Complete(Checkout, _Result(outcome), Head, Noon);

            var verdict = await _Gate().JudgeAsync(Checkout, CancellationToken.None);

            Assert.False(verdict.AllowsWithoutAsking, $"{outcome} was allowed through.");
        }
    }

    [Fact]
    public async Task APassOnTheCommitThatIsCheckedOutIsTheOneThingThatOpensIt()
    {
        _settings.Set(Checkout, on: true);
        _tracker.Complete(Checkout, _Result(LocalRunOutcome.Passed), Head, Noon);

        var verdict = await _Gate().JudgeAsync(Checkout, CancellationToken.None);

        Assert.Equal(GateStatus.Passed, verdict.Status);
        Assert.True(verdict.AllowsWithoutAsking);
    }

    [Fact]
    public async Task APassOnAnEarlierCommitIsNotAPassOnThisOne()
    {
        _settings.Set(Checkout, on: true);
        _tracker.Complete(Checkout, _Result(LocalRunOutcome.Passed), Older, Noon);

        var verdict = await _Gate().JudgeAsync(Checkout, CancellationToken.None);

        // Otherwise the gate waves through everything committed after the last green run, which is most of the
        // work — a guard that is green because it stopped looking.
        Assert.Equal(GateStatus.NotRun, verdict.Status);
        Assert.Contains("22222222", verdict.Reason);
        Assert.Contains("11111111", verdict.Reason);
    }

    [Fact]
    public async Task AnUnreadableCommitIsNotEvidenceOfAnything()
    {
        _settings.Set(Checkout, on: true);
        _tracker.Complete(Checkout, _Result(LocalRunOutcome.Passed), Head, Noon);

        var verdict = await _Gate(gitAnswers: false).JudgeAsync(Checkout, CancellationToken.None);

        Assert.Equal(GateStatus.NotRun, verdict.Status);
        Assert.False(verdict.AllowsWithoutAsking);
    }

    [Fact]
    public async Task ARefusedCheckoutOffersTheOperatorTheWayPastAndRecordsWhy()
    {
        _settings.Set(Checkout, on: true);
        _host.Answer = ConsentOutcome.Approved;

        var answer = await _Intent().HandleAsync(_IntentWith(new Dictionary<string, string> { ["repository"] = Checkout }));

        // The bypass exists, it is explicit, and it goes through the host's consent — which is what puts it in the
        // consent trail with the reason attached. That trail is the only reason a bypass is allowed at all.
        var asked = Assert.Single(_host.Asked);
        Assert.Contains("nothing has been run", asked.Action);
        Assert.Equal(ConsentRisk.Dangerous, asked.Risk);
        Assert.Equal("true", answer[PullRequestGateIntent.AllowedKey]);
        Assert.Equal("bypassed", answer[PullRequestGateIntent.StatusKey]);
    }

    [Fact]
    public async Task DecliningTheWayPastLeavesThePullRequestClosed()
    {
        _settings.Set(Checkout, on: true);
        _host.Answer = ConsentOutcome.Denied;

        var answer = await _Intent().HandleAsync(_IntentWith(new Dictionary<string, string> { ["repository"] = Checkout }));

        Assert.Equal("false", answer[PullRequestGateIntent.AllowedKey]);
        Assert.Equal("notrun", answer[PullRequestGateIntent.StatusKey]);
        Assert.NotEmpty(answer[PullRequestGateIntent.ReasonKey]);
    }

    private PullRequestGate _Gate(bool gitAnswers = true)
    {
        var git = new FakeCliRunner();
        git.Returns("git", gitAnswers ? CliResult.Exited(0, Head, string.Empty) : CliResult.NotStarted);
        return new PullRequestGate(_tracker, _settings, new GitHead(git));
    }

    private PullRequestGateIntent _Intent() => new(_host, _Gate());

    private static PluginIntent _IntentWith(IReadOnlyDictionary<string, string> data) =>
        new("caller", "local-ci", PullRequestGateIntent.Action, data);

    private static LocalRunResult _Result(LocalRunOutcome outcome) =>
        new("ci.yml", "build", outcome, TimeSpan.FromSeconds(131), 0, "a reason", string.Empty);
}
