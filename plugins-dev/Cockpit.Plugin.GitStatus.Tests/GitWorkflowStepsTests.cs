using Cockpit.Plugins.Abstractions.Workflows;
using FluentAssertions;

namespace Cockpit.Plugin.GitStatus.Tests;

/// <summary>
/// The git steps, against a real repository (#69). A fake git would prove nothing: what these steps promise is about
/// what git actually does with a dirty tree, an existing branch, an empty diff.
/// <para>
/// The two rules worth holding open are the refusals. A flow that switches branches with uncommitted work drags that
/// work onto a branch it does not belong to, and a flow that makes empty commits fills a history someone has to read
/// with sentences about nothing.
/// </para>
/// </summary>
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
        if (Directory.Exists(_repo))
        {
            Directory.Delete(_repo, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SwitchingToABranchThatDoesNotExist_CreatesIt()
    {
        var result = await _Run("git.branch", ("Branch", "eve-14-fix-it"), ("Working directory", _repo));

        result.Items[0]["created"].Should().Be("true");
        result.Items[0]["branch"].Should().Be("eve-14-fix-it");
        _Git("rev-parse", "--abbrev-ref", "HEAD").Trim().Should().Be("eve-14-fix-it");
    }

    [Fact]
    public async Task SwitchingToABranchThatExists_JustSwitches()
    {
        _Git("branch", "already-here");

        var result = await _Run("git.branch", ("Branch", "already-here"), ("Working directory", _repo));

        result.Items[0]["created"].Should().Be("false");
    }

    [Fact]
    public async Task SwitchingWithUncommittedWork_IsRefused_RatherThanDraggingItOntoAnotherBranch()
    {
        File.WriteAllText(Path.Combine(_repo, "README.md"), "changed\n");

        var run = async () => await _Run("git.branch", ("Branch", "somewhere-else"), ("Working directory", _repo));

        (await run.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*uncommitted changes*");
    }

    [Fact]
    public async Task Committing_StagesEverything_AndHandsOnTheCommit()
    {
        File.WriteAllText(Path.Combine(_repo, "new.txt"), "a new file\n");

        var result = await _Run("git.commit", ("Message", "added: a new file"), ("Working directory", _repo));

        result.Items[0]["commit"].Should().NotBeEmpty();
        result.Items[0]["branch"].Should().Be("main");
        _Git("log", "-1", "--pretty=%s").Trim().Should().Be("added: a new file");
    }

    [Fact]
    public async Task CommittingWithNothingToCommit_SaysSo_AndMakesNoEmptyCommit()
    {
        var before = _Git("rev-parse", "HEAD").Trim();

        var result = await _Run("git.commit", ("Message", "nothing happened"), ("Working directory", _repo));

        result.Output.Should().Be("Nothing to commit.");
        result.Items.Should().BeEmpty();
        _Git("rev-parse", "HEAD").Trim().Should().Be(before);
    }

    [Fact]
    public async Task AStepWithNoWorkingDirectory_SaysWhatToWrite()
    {
        var run = async () => await _Run("git.commit", ("Message", "x"), ("Working directory", string.Empty));

        (await run.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*{directory}*");
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
