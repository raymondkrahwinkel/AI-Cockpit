using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Terminal;
using FluentAssertions;

namespace Cockpit.Core.Tests.Terminal;

/// <summary>
/// The shell provider is the thin end of the TTY contract (#AC-25): it runs the resolved shell in the session's
/// working directory and adds none of the agent-CLI machinery (no env overlay, no session-scoped files, no status).
/// </summary>
public class ShellTtySessionProviderTests
{
    private static TtyLaunchContext Context(string workingDirectory) =>
        new(
            Profile: null,
            Options: new Dictionary<string, string>(),
            WorkingDirectory: workingDirectory,
            Resume: null,
            BaseEnvironment: new Dictionary<string, string>());

    [Fact]
    public void BuildLaunch_RunsTheShellInTheWorkingDirectoryWithNoAgentMachinery()
    {
        var shell = new ShellDescriptor("pwsh", "PowerShell", @"C:\pwsh\pwsh.exe", ["-NoLogo"]);
        var provider = new ShellTtySessionProvider(shell);

        provider.ProviderId.Should().Be("shell");

        var spec = provider.BuildLaunch(Context(@"C:\work\project"));

        spec.ExecutablePath.Should().Be(@"C:\pwsh\pwsh.exe");
        spec.Arguments.Should().Equal("-NoLogo");
        spec.WorkingDirectory.Should().Be(@"C:\work\project");
        spec.EnvironmentOverlay.Should().BeEmpty();
        spec.SessionScopedFiles.Should().BeEmpty();
        spec.StatusFile.Should().BeNull("a shell has no rate limits or context window to relay");
    }
}
