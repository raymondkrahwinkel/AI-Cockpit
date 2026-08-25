using Cockpit.Core.Abstractions.Shell;
using Cockpit.Infrastructure.Shell;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Shell;

/// <summary>The <c>run_command</c> MCP tool (AC-1066): validates the directory, forwards to the runner, reports the result.</summary>
public sealed class ShellMcpToolsTests
{
    private readonly IShellCommandRunner _runner = Substitute.For<IShellCommandRunner>();
    private readonly ShellMcpTools _tool;

    public ShellMcpToolsTests() => _tool = new ShellMcpTools(_runner);

    [Fact]
    public async Task RunCommand_RunsTheGivenCommand_AndReportsExitCodeAndOutput()
    {
        _runner.RunAsync(Arg.Any<string>(), "rg", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ShellCommandResult(0, "ripgrep 14.1.0", string.Empty, TimeSpan.FromSeconds(1), TimedOut: false));

        var response = await _tool.RunCommand("pane-1", Path.GetTempPath(), "rg", ["--version"]);

        Assert.Contains("\"ok\":true", response);
        Assert.Contains("ripgrep 14.1.0", response);
        Assert.Contains("\"exitCode\":0", response);
    }

    [Fact]
    public async Task RunCommand_WithANonExistentDirectory_RefusesWithoutRunningAnything()
    {
        var response = await _tool.RunCommand("pane-1", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), "rg", ["--version"]);

        Assert.Contains("\"ok\":false", response);
        await _runner.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCommand_ClampsTheTimeout_ToTheDocumentedCeiling()
    {
        _runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ShellCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero, TimedOut: false));

        await _tool.RunCommand("pane-1", Path.GetTempPath(), "sleep", ["1"], timeoutSeconds: 999_999);

        await _runner.Received(1).RunAsync(Arg.Any<string>(), "sleep", Arg.Any<IReadOnlyList<string>>(), TimeSpan.FromSeconds(600), Arg.Any<CancellationToken>());
    }
}
