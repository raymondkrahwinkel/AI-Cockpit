using Cockpit.Plugin.LocalCi.Execution;

namespace Cockpit.Plugin.LocalCi.Tests;

/// <summary>
/// The executing side gets no opinion of its own. Every refusal here has to be the classification's, word for word —
/// a second, gentler judgement at this point is how a job that skipped what it could not do ends up reported green.
/// </summary>
public class LocalRunApprovalTests : IDisposable
{
    private readonly TemporaryProject _project = new();

    public void Dispose() => _project.Dispose();

    [Fact]
    public void APlainLinuxJobIsApprovedWithItsRunnerLabel()
    {
        var workflow = _project.AddWorkflow("ci.yml", TemporaryProject.OneLinuxJob);

        var approval = LocalRunApproval.For(new LocalRunRequest(_project.Root, workflow, "build"));

        Assert.True(approval.IsApproved, approval.Reason);
        Assert.Equal("ubuntu-latest", approval.RunnerLabel);
    }

    [Fact]
    public void AJobTheClassificationRefusesCarriesItsReasonUnchanged()
    {
        var workflow = _project.AddWorkflow("ci.yml", TemporaryProject.MatrixJob);

        var approval = LocalRunApproval.For(new LocalRunRequest(_project.Root, workflow, "spread"));

        Assert.False(approval.IsApproved);
        Assert.Contains("matrix", approval.Reason);
    }

    [Fact]
    public void AJobThatIsNotInTheFileIsRefusedRatherThanAttempted()
    {
        var workflow = _project.AddWorkflow("ci.yml", TemporaryProject.OneLinuxJob);

        var approval = LocalRunApproval.For(new LocalRunRequest(_project.Root, workflow, "nope"));

        Assert.False(approval.IsApproved);
        Assert.Contains("no job called nope", approval.Reason);
    }

    [Fact]
    public void AWorkflowOutsideTheProjectIsRefused()
    {
        _project.AddWorkflow("ci.yml", TemporaryProject.OneLinuxJob);
        var elsewhere = Path.Combine(_project.Root, ".github", "workflows", "somewhere-else.yml");

        var approval = LocalRunApproval.For(new LocalRunRequest(_project.Root, elsewhere, "build"));

        Assert.False(approval.IsApproved);
        Assert.Contains("not one of this project's workflows", approval.Reason);
    }

    [Fact]
    public void AWorkflowThatDoesNotParseIsRefusedWithTheReadingError()
    {
        var workflow = _project.AddWorkflow("ci.yml", "jobs: [this is not a mapping");

        var approval = LocalRunApproval.For(new LocalRunRequest(_project.Root, workflow, "build"));

        Assert.False(approval.IsApproved);
        Assert.NotEmpty(approval.Reason);
    }
}
