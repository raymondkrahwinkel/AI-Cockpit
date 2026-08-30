using Cockpit.Core.Terminal;

namespace Cockpit.Core.Tests.Terminal;

/// <summary>
/// Elevation is a Windows-only path (AC-967): `ShellExecuteEx`+`runas` has no meaning elsewhere, so on any other
/// platform the launcher refuses before it can spawn anything, and the UI that offers it is hidden.
/// </summary>
public class ElevatedTerminalLauncherTests
{
    private static readonly ShellDescriptor Shell =
        new("pwsh", "PowerShell", OperatingSystem.IsWindows() ? @"C:\nope\pwsh.exe" : "/nope/pwsh", ["-NoLogo"]);

    [Fact]
    public void IsSupported_MatchesWindows()
    {
        Assert.Equal(OperatingSystem.IsWindows(), ElevatedTerminalLauncher.IsSupported);
    }

    [Fact]
    public void Launch_OnNonWindows_RefusesInsteadOfStartingAnything()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal("Starting a terminal as administrator is a Windows-only action.", ElevatedTerminalLauncher.Launch(Shell));
    }

    [Fact]
    public void Launch_ReportsFailureInsteadOfThrowing()
    {
        // A shell path that cannot exist: the operator must get a message, never an unhandled exception.
        Assert.NotNull(ElevatedTerminalLauncher.Launch(Shell));
    }
}
