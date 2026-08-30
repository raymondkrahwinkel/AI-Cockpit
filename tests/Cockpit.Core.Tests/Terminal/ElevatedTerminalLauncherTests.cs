using Cockpit.Core.Terminal;
using ElevatedTerminalLauncher = Cockpit.App.Services.ElevatedTerminalLauncher;

namespace Cockpit.Core.Tests.Terminal;

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

}
