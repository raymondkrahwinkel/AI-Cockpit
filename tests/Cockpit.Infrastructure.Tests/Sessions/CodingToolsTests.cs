using System.Text.RegularExpressions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Shell;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Sessions;

// AC-1489: the cockpit-coding read pages a large file without overlap or gap, and nothing outside the calling
// pane's working directory is reachable.
public class CodingToolsTests
{
    [Fact]
    public async Task Read_PagesALargeFileWithoutOverlapOrGap_AndRefusesPathsOutsideTheWorkingDirectory()
    {
        var parent = Directory.CreateTempSubdirectory("ac1489-");
        var root = Directory.CreateDirectory(Path.Combine(parent.FullName, "worktree")).FullName;
        File.WriteAllLines(Path.Combine(root, "big.txt"), Enumerable.Range(1, 3000).Select(line => $"line {line} " + new string('x', 30)));
        var outside = Path.Combine(parent.FullName, "outside.txt");
        File.WriteAllText(outside, "secret");

        var session = Substitute.For<ISessionHandle>();
        session.WorkingDirectory.Returns(root);
        var sessions = Substitute.For<ISessionRegistry>();
        sessions.Find(Arg.Any<string>()).Returns(call => call.Arg<string>() == "pane-1" ? session : null);
        var runner = Substitute.For<IShellCommandRunner>();
        var tools = new CodingTools(sessions, runner);
        var shell = new ShellMcpTools(runner, sessions);
        var codingShell = new CodingShellTools(sessions, runner);

        McpRequestContext.Set("pane-1");
        try
        {
            var first = tools.Read("big.txt");
            var next = int.Parse(Regex.Match(first, @"Use offset=(\d+) to continue").Groups[1].Value);
            var second = tools.Read("big.txt", offset: next);

            // A page stays within the cap and says where to go on; the next page starts on exactly that line.
            Assert.InRange(first.Length, 1, CodingTools.MaxChars + 200);
            Assert.Contains($"\n{next - 1}: line {next - 1} ", first);
            Assert.DoesNotContain($"\n{next}: ", first);
            Assert.StartsWith($"{next}: line {next} ", second);

            Assert.StartsWith("Error:", tools.Read(Path.Combine("..", "outside.txt")));
            Assert.StartsWith("Error:", tools.Read(outside));
            Assert.DoesNotContain("secret", tools.Read(outside));

            Assert.Contains("malformed tool-call fragment", await shell.RunCommand("pane-1", root, "echo", ["<parameter=directory>"], 1), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Invalid session", await shell.RunCommand("other-pane", root, "echo", timeoutSeconds: 1));
            Assert.Contains("malformed tool-call fragment", await codingShell.Shell("echo <function=run_command>"), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("malformed tool-call fragment", await codingShell.Shell("echo ok", workdir: "</tool_call>"), StringComparison.OrdinalIgnoreCase);

            McpRequestContext.Set(null);
            Assert.Contains("Unknown session", await shell.RunCommand("other-pane", root, "echo", timeoutSeconds: 1));
            await runner.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>());
        }
        finally
        {
            McpRequestContext.Set(null);
            parent.Delete(recursive: true);
        }
    }
}
