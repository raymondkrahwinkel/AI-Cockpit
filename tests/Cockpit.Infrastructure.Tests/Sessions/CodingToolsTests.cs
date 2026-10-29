using System.Text.RegularExpressions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Shell;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Sessions;

// AC-1489: the cockpit-coding read pages a large file without overlap or gap, and nothing outside the calling
// pane's working directory is reachable.
public class CodingToolsTests
{
    [Fact]
    public async Task Read_PagesALargeFileWithoutOverlapOrGap_AndRefusesPathsOutsideTheWorkingDirectory_AndSelectsTheAvailableShell()
    {
        var parent = Directory.CreateTempSubdirectory("ac1489-");
        var root = Directory.CreateDirectory(Path.Combine(parent.FullName, "worktree")).FullName;
        File.WriteAllLines(Path.Combine(root, "big.txt"), Enumerable.Range(1, 3000).Select(line => $"line {line} " + new string('x', 30)));
        var outside = Path.Combine(parent.FullName, "outside.txt");
        File.WriteAllText(outside, "secret");

        var session = Substitute.For<ISessionHandle>();
        session.WorkingDirectory.Returns(root);
        var sessions = Substitute.For<ISessionRegistry>();
        sessions.Find("pane-1").Returns(session);
        var tools = new CodingTools(sessions, Substitute.For<IShellCommandRunner>());
        var runner = Substitute.For<IShellCommandRunner>();
        var invocations = new List<(string Shell, IReadOnlyList<string> Arguments)>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                invocations.Add((call.ArgAt<string>(1), call.ArgAt<IReadOnlyList<string>>(2)));
                return new ShellCommandResult(0, "ok", "", TimeSpan.Zero, false);
            });
        var codingShell = new CodingShellTools(sessions, runner, executable => executable == "pwsh");

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

            await codingShell.Shell("echo ok");
            if (OperatingSystem.IsWindows())
            {
                await new CodingShellTools(sessions, runner, _ => false).Shell("echo ok");
                Assert.Equal(["pwsh", "powershell.exe"], invocations.Select(invocation => invocation.Shell));
                Assert.All(invocations, invocation => Assert.Equal(["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "echo ok"], invocation.Arguments));
            }
            else
            {
                Assert.Equal("/bin/bash", invocations.Single().Shell);
                Assert.Equal(["-lc", "echo ok"], invocations.Single().Arguments);
            }
        }
        finally
        {
            McpRequestContext.Set(null);
            parent.Delete(recursive: true);
        }
    }
}
