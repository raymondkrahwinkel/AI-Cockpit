using Microsoft.Extensions.Options;
using NSubstitute;
using FluentAssertions;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Configuration;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Claude;
using Cockpit.Infrastructure.Claude.Tty;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Covers <see cref="ClaudeTtyLauncher"/>'s composition seam: it must resolve the executable path,
/// mark the working directory trusted, build the pty environment, and hand all of that to whichever
/// <see cref="IPtyHostFactory"/> DI wired in — without caring which platform that factory targets.
/// The real pty spawn is out of unit-test reach (needs a real ConPTY/Porta.Pty and a logged-in CLI).
/// </summary>
public class ClaudeTtyLauncherTests : IDisposable
{
    private readonly string _configDir = Directory.CreateTempSubdirectory("cockpit-tty-launcher-tests-").FullName;

    [Fact]
    public void Launch_WithAProfile_PassesTheProfileConfigDirAndTermToThePtyHostFactory()
    {
        var executableLocator = Substitute.For<IClaudeExecutableLocator>();
        var ptyHostFactory = Substitute.For<IPtyHostFactory>();
        var expectedPty = Substitute.For<IConPtyProcess>();
        ptyHostFactory
            .Start(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<short>(), Arg.Any<short>())
            .Returns(expectedPty);

        var launcher = new ClaudeTtyLauncher(
            Options.Create(new CockpitOptions()),
            executableLocator,
            new WorkspaceTrustWriter(),
            ptyHostFactory);
        var profile = new ClaudeProfile("Personal", _configDir, ExecutablePath: "/usr/bin/claude");

        var pty = launcher.Launch(profile, columns: 120, rows: 40);

        pty.Should().BeSameAs(expectedPty);
        ptyHostFactory.Received(1).Start(
            "/usr/bin/claude",
            Arg.Any<string>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(env =>
                env["TERM"] == "xterm-256color" && env["CLAUDE_CONFIG_DIR"] == _configDir),
            120,
            40);
    }

    [Fact]
    public void Launch_WithAProfile_MarksTheWorkingDirectoryTrustedBeforeSpawning()
    {
        var executableLocator = Substitute.For<IClaudeExecutableLocator>();
        var ptyHostFactory = Substitute.For<IPtyHostFactory>();
        ptyHostFactory
            .Start(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<short>(), Arg.Any<short>())
            .Returns(Substitute.For<IConPtyProcess>());

        var launcher = new ClaudeTtyLauncher(
            Options.Create(new CockpitOptions { Claude = new ClaudeCliOptions { WorkingDirectory = Directory.GetCurrentDirectory() } }),
            executableLocator,
            new WorkspaceTrustWriter(),
            ptyHostFactory);
        var profile = new ClaudeProfile("Personal", _configDir);

        launcher.Launch(profile, columns: 80, rows: 24);

        var claudeJson = File.ReadAllText(Path.Combine(_configDir, ".claude.json"));
        claudeJson.Should().Contain(Path.GetFullPath(Directory.GetCurrentDirectory()).Replace("\\", "\\\\"));
    }

    [Fact]
    public void Launch_WithoutAProfile_FallsBackToTheBundledOrConfiguredExecutable()
    {
        var executableLocator = Substitute.For<IClaudeExecutableLocator>();
        executableLocator.FindBundledExecutable().Returns("/opt/claude/claude");
        var ptyHostFactory = Substitute.For<IPtyHostFactory>();
        ptyHostFactory
            .Start(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<short>(), Arg.Any<short>())
            .Returns(Substitute.For<IConPtyProcess>());

        var launcher = new ClaudeTtyLauncher(
            Options.Create(new CockpitOptions()),
            executableLocator,
            new WorkspaceTrustWriter(),
            ptyHostFactory);

        launcher.Launch(profile: null, columns: 80, rows: 24);

        ptyHostFactory.Received(1).Start(
            "/opt/claude/claude",
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            80,
            24);
    }

    public void Dispose() => Directory.Delete(_configDir, recursive: true);
}
