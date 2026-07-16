using Cockpit.Core.Terminal;
using FluentAssertions;

namespace Cockpit.Core.Tests.Terminal;

/// <summary>
/// Shell detection for terminal panes (#AC-25): a shell is only offered once it resolves to a real file, the
/// preferred one leads, a bare name gets Windows extension probing, and the same binary is never listed twice.
/// Driven with real files in a temp directory on the PATH, so it exercises the actual filesystem resolution on
/// whichever OS the test runs — it does not simulate a foreign OS, because <see cref="ShellCatalog.Detect"/> never
/// runs on one.
/// </summary>
public sealed class ShellCatalogTests : IDisposable
{
    private readonly string _dir;

    public ShellCatalogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cockpit-shell-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string Touch(string fileName)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public void Build_ResolvesAShellOnPathToAnAbsolutePathAndLeadsWithThePreferredOne()
    {
        if (OperatingSystem.IsWindows())
        {
            // A bare "pwsh" candidate must find pwsh.exe via extension probing (Process does no PATHEXT lookup).
            var pwsh = Touch("pwsh.exe");

            var shells = ShellCatalog.Build(_dir, shellEnvironmentVariable: null, comSpec: null);

            shells[0].Id.Should().Be("pwsh", "PowerShell 7 is the preferred Windows default");
            shells[0].DisplayName.Should().Be("PowerShell");
            shells[0].ExecutablePath.Should().Be(pwsh);
        }
        else
        {
            var bash = Touch("bash");

            var shells = ShellCatalog.Build(_dir, shellEnvironmentVariable: null, comSpec: null);

            shells.Should().Contain(s => s.Id == "bash" && s.ExecutablePath == bash);
        }
    }

    [Fact]
    public void Build_DropsCandidatesThatDoNotResolve()
    {
        // An empty directory on PATH and no $SHELL/%COMSPEC%: nothing resolves, so nothing is offered rather than a
        // dead path.
        var shells = ShellCatalog.Build(_dir, shellEnvironmentVariable: null, comSpec: null);

        shells.Should().BeEmpty();
    }

    [Fact]
    public void Build_Windows_IncludesCmdFromComSpec()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cmd = Touch("cmd.exe");

        var shells = ShellCatalog.Build(_dir, shellEnvironmentVariable: null, comSpec: cmd);

        shells.Should().ContainSingle(s => s.Id == "cmd").Which.ExecutablePath.Should().Be(cmd);
    }

    [Fact]
    public void Build_Unix_LoginShellLeadsAndIsNotListedTwiceWhenItIsAlsoACandidate()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // $SHELL points at the same bash the "bash" candidate also finds — it must appear once, as the leading login.
        var bash = Touch("bash");

        var shells = ShellCatalog.Build(_dir, shellEnvironmentVariable: bash, comSpec: null);

        shells[0].Id.Should().Be("login");
        shells[0].DisplayName.Should().Be("bash");
        shells[0].ExecutablePath.Should().Be(bash);
        shells.Where(s => s.ExecutablePath == bash).Should().ContainSingle("the login shell and the bash candidate are the same file");
    }

    [Fact]
    public void ForCommand_Blank_ReturnsNull()
    {
        ShellCatalog.ForCommand("   ").Should().BeNull();
    }

    [Fact]
    public void ForCommand_ResolvesAnExistingPathAndNamesItByItsFileName()
    {
        var fish = Touch(OperatingSystem.IsWindows() ? "fish.exe" : "fish");

        var descriptor = ShellCatalog.ForCommand(fish);

        descriptor.Should().NotBeNull();
        descriptor!.Id.Should().Be("custom");
        descriptor.DisplayName.Should().Be("fish");
        descriptor.ExecutablePath.Should().Be(fish);
        descriptor.Arguments.Should().BeEmpty();
    }

    [Fact]
    public void ForCommand_UnresolvedPath_PassesThroughSoThePtySurfacesTheError()
    {
        // A path that does not exist is passed through unchanged rather than silently swapped for another shell —
        // the pty then reports a real "not found" the operator can fix.
        var missing = Path.Combine(_dir, OperatingSystem.IsWindows() ? "nope.exe" : "nope");

        var descriptor = ShellCatalog.ForCommand(missing);

        descriptor.Should().NotBeNull();
        descriptor!.ExecutablePath.Should().Be(missing);
        descriptor.DisplayName.Should().Be("nope");
    }
}
