using Cockpit.Plugin.LocalCi.Workflows;

namespace Cockpit.Plugin.LocalCi.Tests;

// The classification run against this repository's own workflows. Hand-written YAML proves each rule in isolation;
// this proves the rules together say something true about a real project — and it is the check that fails when a
// workflow here grows a construct the classifier has never seen.
public class RealWorkflowsTests
{
    private static readonly IReadOnlyList<WorkflowParseResult> Workflows = WorkflowCatalog.ReadProject(_RepositoryRoot());

    [Fact]
    public void EveryWorkflowInThisRepositoryParses()
    {
        Assert.NotEmpty(Workflows);
        Assert.All(Workflows, workflow => Assert.Null(workflow.Error));
    }

    [Fact]
    public void CiHasExactlyFiveLocallyRunnableJobs()
    {
        var verdicts = _VerdictsFor("ci.yml");

        Assert.Equal(5, verdicts.Count(verdict => verdict.CanRunLocally));
        Assert.Equal(["changes", "build", "plugins", "plugin-versions", "xmldoc-scope"], verdicts.Where(v => v.CanRunLocally).Select(v => v.JobId));
    }

    [Theory]
    [InlineData("nightly.yml", "publish", "it uses a matrix")]
    [InlineData("release.yml", "publish", "it uses a matrix")]
    [InlineData("nightly.yml", "release", "exchanges artifacts with another job")]
    [InlineData("release.yml", "changelog", "exchanges artifacts with another job")]
    public void MatrixAndArtifactJobsAreRefusedWithTheRightReason(string workflow, string jobId, string reason)
    {
        var verdict = _VerdictsFor(workflow).Single(v => v.JobId == jobId);

        Assert.False(verdict.CanRunLocally);
        Assert.Contains(reason, verdict.Reason);
    }

    [Theory]
    [InlineData("nightly.yml", "changes")]
    [InlineData("release.yml", "gate")]
    [InlineData("release.yml", "finalize")]
    [InlineData("publish-plugin.yml", "publish")]
    [InlineData("publish-updated-plugins.yml", "discover")]
    public void PlainLinuxJobsInThisRepositoryCanRunLocally(string workflow, string jobId)
    {
        var verdict = _VerdictsFor(workflow).Single(v => v.JobId == jobId);

        Assert.True(verdict.CanRunLocally, verdict.Reason);
    }

    // The first job in this repository that calls another workflow instead of carrying steps of its own. It is
    // pinned here because a job with no `steps:` is the shape a classifier is most likely to wave through by
    // accident — reading "nothing here refuses it" as "it can run" — and act cannot run one at all.
    [Fact]
    public void AJobThatCallsAnotherWorkflowIsRefused()
    {
        var verdict = _VerdictsFor("publish-updated-plugins.yml").Single(v => v.JobId == "publish");

        Assert.False(verdict.CanRunLocally);
    }

    private static IReadOnlyList<JobVerdict> _VerdictsFor(string fileName)
    {
        var workflow = Workflows.Single(w => Path.GetFileName(w.Path) == fileName);
        Assert.Null(workflow.Error);
        return LocalRunClassifier.Classify(workflow.Document!);
    }

    // Walks up from the test binary until it finds the checkout. The test project sits at a known depth, but the
    // build output does not, and CI and a local run put it in different places.
    private static string _RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".github", "workflows")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"No .github/workflows found above {AppContext.BaseDirectory}; these tests read this repository's own workflows.");
    }
}
