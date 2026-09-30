using Cockpit.Plugins.Abstractions.Workflows;
using Cockpit.TestSupport;

namespace Cockpit.Plugin.GitStatus.Tests;

// The git steps, against a real repository (#69). A fake git would prove nothing: what these steps promise is about
// what git actually does with a dirty tree, an existing branch, an empty diff.
//
// The two rules worth holding open are the refusals. A flow that switches branches with uncommitted work drags that
// work onto a branch it does not belong to, and a flow that makes empty commits fills a history someone has to read
// with sentences about nothing.
public class GitWorkflowStepsTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"cockpit-git-{Guid.NewGuid():n}");

    public GitWorkflowStepsTests()
    {
        Directory.CreateDirectory(_repo);

        _Git("init", "-b", "main");
        _Git("config", "user.email", "test@example.com");
        _Git("config", "user.name", "Test");

        File.WriteAllText(Path.Combine(_repo, "README.md"), "hello\n");
        _Git("add", "-A");
        _Git("commit", "-m", "first");
    }

    public void Dispose()
    {
        // Every one of these tests passed and then failed in teardown on Windows, which reads as six broken git
        // steps rather than one unremovable directory.
        TestGitDirectory.Remove(_repo);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SwitchingWithUncommittedWork_IsRefused_RatherThanDraggingItOntoAnotherBranch()
    {
        File.WriteAllText(Path.Combine(_repo, "README.md"), "changed\n");

        var run = async () => await _Run("git.branch", ("Branch", "somewhere-else"), ("Working directory", _repo));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(run);
        Assert.Contains("uncommitted changes", ex.Message);
    }

    private static async Task<WorkflowStepResult> _Run(string typeId, params (string Name, string Value)[] parameters)
    {
        var step = GitWorkflowSteps.All().Single(candidate => candidate.TypeId == typeId);

        var context = new WorkflowStepContext(
            parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Value, StringComparer.Ordinal),
            []);

        return await step.RunAsync(context, CancellationToken.None);
    }

    private string _Git(params string[] arguments) =>
        GitCommand.RunAsync(_repo, arguments, CancellationToken.None).GetAwaiter().GetResult();
}
