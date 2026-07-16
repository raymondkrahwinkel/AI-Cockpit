using Cockpit.Core.Terminal;
using FluentAssertions;

namespace Cockpit.Core.Tests.Terminal;

/// <summary>
/// Per-OS shell detection for terminal panes (#AC-25): only shells that resolve to a real file are offered, the
/// preferred one leads, a bare name gets Windows extension probing, and the same binary is never listed twice.
/// </summary>
public class ShellCatalogTests
{
    private static Func<string, bool> Exists(params string[] present)
    {
        var set = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    [Fact]
    public void Build_Windows_ResolvesPwshWithExeExtensionAndLeadsWithIt()
    {
        var pwsh = Path.Combine(@"C:\pwsh", "pwsh") + ".exe";

        var shells = ShellCatalog.Build(
            isWindows: true,
            pathVariable: @"C:\pwsh",
            shellEnvironmentVariable: null,
            comSpec: @"C:\Windows\System32\cmd.exe",
            fileExists: Exists(pwsh, @"C:\Windows\System32\cmd.exe"));

        shells[0].Id.Should().Be("pwsh");
        shells[0].DisplayName.Should().Be("PowerShell");
        shells[0].ExecutablePath.Should().Be(pwsh);
        shells[0].Arguments.Should().ContainSingle().Which.Should().Be("-NoLogo");

        // Windows PowerShell and WSL were not on the probe, so they are dropped rather than offered as a dead path.
        shells.Select(s => s.Id).Should().NotContain("powershell").And.NotContain("wsl");
        // cmd is always reachable through %COMSPEC%, taken as a rooted path.
        shells.Should().ContainSingle(s => s.Id == "cmd").Which.ExecutablePath.Should().Be(@"C:\Windows\System32\cmd.exe");
    }

    [Fact]
    public void Build_Windows_NoComSpec_FallsBackToBareCmd()
    {
        var cmd = Path.Combine(@"C:\Windows\System32", "cmd") + ".exe";

        var shells = ShellCatalog.Build(
            isWindows: true,
            pathVariable: @"C:\Windows\System32",
            shellEnvironmentVariable: null,
            comSpec: null,
            fileExists: Exists(cmd));

        shells.Should().ContainSingle(s => s.Id == "cmd").Which.ExecutablePath.Should().Be(cmd);
    }

    [Fact]
    public void Build_Unix_LoginShellLeadsAndUsesItsFileName()
    {
        var bash = Path.Combine("/usr/bin", "bash");

        var shells = ShellCatalog.Build(
            isWindows: false,
            pathVariable: "/usr/bin",
            shellEnvironmentVariable: "/bin/zsh",
            comSpec: null,
            fileExists: Exists("/bin/zsh", bash));

        shells[0].Id.Should().Be("login");
        shells[0].DisplayName.Should().Be("zsh");
        shells[0].ExecutablePath.Should().Be("/bin/zsh");
        shells.Should().Contain(s => s.Id == "bash" && s.ExecutablePath == bash);
    }

    [Fact]
    public void Build_Unix_LoginShellSameBinaryAsCandidate_IsNotListedTwice()
    {
        var bash = Path.Combine("/usr/bin", "bash");

        var shells = ShellCatalog.Build(
            isWindows: false,
            pathVariable: "/usr/bin",
            shellEnvironmentVariable: bash,
            comSpec: null,
            fileExists: Exists(bash));

        shells.Where(s => s.ExecutablePath == bash).Should().ContainSingle("the login shell and the bash candidate resolve to the same file");
        shells[0].Id.Should().Be("login");
    }

    [Fact]
    public void ForCommand_Blank_ReturnsNull()
    {
        ShellCatalog.ForCommand("   ").Should().BeNull();
    }

    [Fact]
    public void ForCommand_UnresolvedPath_PassesThroughSoThePtySurfacesTheError()
    {
        // A rooted path that does not exist resolves to nothing, so it is passed through unchanged rather than
        // silently swapped for another shell — the pty then reports a real "not found" the operator can fix.
        var descriptor = ShellCatalog.ForCommand("/opt/definitely-not-here/fish");

        descriptor.Should().NotBeNull();
        descriptor!.Id.Should().Be("custom");
        descriptor.DisplayName.Should().Be("fish");
        descriptor.ExecutablePath.Should().Be("/opt/definitely-not-here/fish");
        descriptor.Arguments.Should().BeEmpty();
    }

    [Fact]
    public void Build_NothingResolves_ReturnsEmpty()
    {
        var shells = ShellCatalog.Build(
            isWindows: true,
            pathVariable: @"C:\nope",
            shellEnvironmentVariable: null,
            comSpec: null,
            fileExists: Exists());

        shells.Should().BeEmpty();
    }
}
