using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Shell;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Shell;

public class CodingShellToolsTests
{
    [Fact]
    public async Task ShellTools_ValidateInputAndUseAvailableWindowsPowerShell()
    {
        var root = Directory.CreateTempSubdirectory("ac1940-");
        var session = Substitute.For<ISessionHandle>();
        session.WorkingDirectory.Returns(root.FullName);
        var sessions = Substitute.For<ISessionRegistry>();
        sessions.Find("pane-1").Returns(session);
        var runner = Substitute.For<IShellCommandRunner>();
        var invocations = new List<(string Shell, IReadOnlyList<string> Arguments)>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                invocations.Add((call.ArgAt<string>(1), call.ArgAt<IReadOnlyList<string>>(2)));
                return new ShellCommandResult(0, "ok", "", TimeSpan.Zero, false);
            });

        McpRequestContext.Set("pane-1");
        try
        {
            var shellMcp = new ShellMcpTools(runner);
            var codingShell = new CodingShellTools(sessions, runner, executable => executable == "pwsh");

            Assert.Contains("malformed tool-call fragment", await shellMcp.RunCommand("pane-1", root.FullName, "echo", ["<parameter=directory>"], 1), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("malformed tool-call fragment", await codingShell.Shell("echo <function=run_command>"), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("malformed tool-call fragment", await codingShell.Shell("echo ok", workdir: "</tool_call>"), StringComparison.OrdinalIgnoreCase);

            var staleSession = await shellMcp.RunCommand("other-pane", root.FullName, "echo", timeoutSeconds: 1);
            Assert.Contains("\"ok\":true", staleSession);
            Assert.Contains("\"session\":\"pane-1\"", staleSession);

            McpRequestContext.Set(null);
            var unverified = await shellMcp.RunCommand("other-pane", root.FullName, "echo", timeoutSeconds: 1);
            Assert.Contains("\"session\":\"other-pane\"", unverified);
            McpRequestContext.Set("pane-1");

            await new CodingShellTools(sessions, runner, executable => executable == "pwsh").Shell("echo ok");
            var codingShellInvocations = invocations.Where(invocation => invocation.Shell != "echo");
            if (OperatingSystem.IsWindows())
            {
                await new CodingShellTools(sessions, runner, _ => false).Shell("echo ok");
                Assert.Equal(["pwsh", "powershell.exe"], codingShellInvocations.Select(invocation => invocation.Shell));
                Assert.All(codingShellInvocations, invocation => Assert.Equal(["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "echo ok"], invocation.Arguments));
            }
            else
            {
                Assert.Equal("/bin/bash", codingShellInvocations.Single().Shell);
                Assert.Equal(["-lc", "echo ok"], codingShellInvocations.Single().Arguments);
            }

            await runner.Received(2).RunAsync(root.FullName, "echo", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>());
        }
        finally
        {
            McpRequestContext.Set(null);
            root.Delete(recursive: true);
        }
    }
}
