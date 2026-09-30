using Cockpit.Plugin.LocalCi.Execution;

namespace Cockpit.Plugin.LocalCi.Tests;

public class ActCommandTests
{
    private static readonly LocalRunRequest Request = new(
        ProjectRoot: Path.Combine("C:", "work", "cockpit"),
        WorkflowPath: Path.Combine("C:", "work", "cockpit", ".github", "workflows", "ci.yml"),
        JobId: "plugins");

    private static readonly ActRunOptions Options = new("catthehacker/ubuntu:act-latest", CpuLimit: 8);

    [Fact]
    public void TheWorkflowIsNamedRelativeToTheCheckout()
    {
        var arguments = ActCommand.Build(Request, "ubuntu-latest", Options, "r1");

        Assert.Equal(".github/workflows/ci.yml", _After(arguments, "-W"));
    }

    [Fact]
    public void TheCheckoutIsTheWorkingDirectoryAndTheJobIsNamed()
    {
        var arguments = ActCommand.Build(Request, "ubuntu-latest", Options, "r1");

        Assert.Equal(Request.ProjectRoot, _After(arguments, "-C"));
        Assert.Equal("plugins", _After(arguments, "-j"));
    }

    private static string _After(IReadOnlyList<string> arguments, string flag)
    {
        var index = arguments.ToList().IndexOf(flag);
        Assert.True(index >= 0 && index + 1 < arguments.Count, $"{flag} is not in the command.");
        return arguments[index + 1];
    }
}
