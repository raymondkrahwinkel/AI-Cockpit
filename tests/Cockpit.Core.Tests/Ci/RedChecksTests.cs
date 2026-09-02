using Cockpit.Core.Ci;

namespace Cockpit.Core.Tests.Ci;

public class RedChecksTests
{
    private const string GhOutput = """
        [
          {"bucket":"pass","link":"https://github.com/o/r/actions/runs/1/job/1","name":"build","workflow":"CI"},
          {"bucket":"fail","link":"https://github.com/o/r/actions/runs/1/job/2","name":"plugins","workflow":"CI"},
          {"bucket":"pending","link":"","name":"xmldoc-scope","workflow":"CI"}
        ]
        """;

    [Fact]
    public void ReadsWhatGhSaid_AndCallsOnlyTheFailBucketRed()
    {
        var checks = RedChecks.Parse(GhOutput);

        // Three checks in, and only the failed one is red — the pending `xmldoc-scope` is the half that matters,
        // because gh reports a run that has merely started with the same non-zero exit as a failure, and reading
        // that as red would mean an alarm on every run.
        Assert.Equal(3, checks.Count);
        Assert.Equal(["plugins"], checks.Where(check => check.IsRed).Select(check => check.Name));
        Assert.Equal("https://github.com/o/r/actions/runs/1/job/2", checks.Single(check => check.IsRed).Link);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("""{"message":"no pull requests found"}""")]
    public void AnAnswerItCannotRead_IsNoChecksRatherThanAGuess(string output) =>
        Assert.Empty(RedChecks.Parse(output));

    [Fact]
    public void ARedCheckIsNewsOnce_AndThenStaysQuietWhileItStaysRed()
    {
        var checks = RedChecks.Parse(GhOutput);

        var first = RedChecks.NewlyRed(checks, new HashSet<string>(StringComparer.Ordinal));
        Assert.Equal(["plugins"], first.Select(check => check.Name));

        var second = RedChecks.NewlyRed(checks, RedChecks.RedNames(checks));
        Assert.Empty(second);
    }

    // A check that was fixed and broke again is news a second time — which only holds because what is remembered is
    // replaced by the current red set rather than added to.
    [Fact]
    public void ACheckThatGoesGreenAndFailsAgain_IsNewsAgain()
    {
        var red = RedChecks.Parse(GhOutput);
        var green = RedChecks.Parse("""[{"bucket":"pass","link":"","name":"plugins","workflow":"CI"}]""");

        var remembered = RedChecks.RedNames(red);
        Assert.Equal(["plugins"], remembered);

        remembered = RedChecks.RedNames(green);
        Assert.Empty(remembered);

        Assert.Equal(["plugins"], RedChecks.NewlyRed(red, remembered).Select(check => check.Name));
    }

    [Fact]
    public void ADifferentCheckFailing_IsItsOwnNews()
    {
        var checks = RedChecks.Parse("""
            [
              {"bucket":"fail","link":"","name":"plugins","workflow":"CI"},
              {"bucket":"fail","link":"","name":"build","workflow":"CI"}
            ]
            """);

        var newly = RedChecks.NewlyRed(checks, new HashSet<string>(StringComparer.Ordinal) { "plugins" });

        Assert.Equal(["build"], newly.Select(check => check.Name));
    }

    // AC-645. A skipped check ran on purpose and blocks nothing; a pending one is a run that can still go red.
    [Theory]
    [InlineData("""[{"bucket":"pass","name":"build"},{"bucket":"skipping","name":"docs"}]""", true)]
    [InlineData("""[{"bucket":"pass","name":"build"},{"bucket":"pending","name":"docs"}]""", false)]
    [InlineData("""[{"bucket":"pass","name":"build"},{"bucket":"cancel","name":"docs"}]""", false)]
    [InlineData("""[{"bucket":"fail","name":"build"}]""", false)]
    [InlineData("[]", false)]
    public void AllGreen_IsEveryCheckInAndNoneOfThemRedOrStillRunning(string json, bool expected) =>
        Assert.Equal(expected, RedChecks.AllGreen(RedChecks.Parse(json)));

    // AC-645, criterion 4: green checks are one question, "may this be merged" is another. The last three rows are
    // what gh prints when there is no pull request, no login, or nothing at all — not ready, so never a report.
    [Theory]
    [InlineData("""{"mergeable":"MERGEABLE","reviewDecision":""}""", true)]
    [InlineData("""{"mergeable":"MERGEABLE","reviewDecision":"APPROVED"}""", true)]
    [InlineData("""{"mergeable":"MERGEABLE","reviewDecision":"CHANGES_REQUESTED"}""", false)]
    [InlineData("""{"mergeable":"MERGEABLE","reviewDecision":"REVIEW_REQUIRED"}""", false)]
    [InlineData("""{"mergeable":"CONFLICTING","reviewDecision":"APPROVED"}""", false)]
    [InlineData("""{"mergeable":"UNKNOWN","reviewDecision":"APPROVED"}""", false)]
    [InlineData("", false)]
    [InlineData("not json", false)]
    [InlineData("[]", false)]
    public void IsReadyToMerge_MeansNothingIsBlockingIt(string json, bool expected) =>
        Assert.Equal(expected, RedChecks.ParseMergeState(json).IsReadyToMerge);
}
