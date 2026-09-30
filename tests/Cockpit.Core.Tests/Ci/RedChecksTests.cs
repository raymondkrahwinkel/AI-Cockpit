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

}
