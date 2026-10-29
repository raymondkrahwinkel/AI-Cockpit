using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Shell;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Shell;

public class CodingShellToolsTests
{
    [Fact]
    public async Task Shell_PrefersPwshAndFallsBackToWindowsPowerShell()
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
            await new CodingShellTools(sessions, runner, executable => executable == "pwsh").Shell("echo ok");
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
            root.Delete(recursive: true);
        }
    }
}
