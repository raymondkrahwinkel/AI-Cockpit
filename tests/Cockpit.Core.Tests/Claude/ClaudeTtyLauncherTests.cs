using Microsoft.Extensions.Options;
using NSubstitute;
using FluentAssertions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Configuration;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Covers <see cref="ClaudeTtyLauncher"/>'s composition seam: it must resolve the executable path,
/// mark the working directory trusted, build the pty environment and launch-only start-default
/// arguments (permission mode/model/effort), and hand all of that to whichever
/// <see cref="IPtyHostFactory"/> DI wired in — without caring which platform that factory targets.
/// The real pty spawn is out of unit-test reach (needs a real ConPTY/Porta.Pty and a logged-in CLI).
/// </summary>
public class ClaudeTtyLauncherTests : IDisposable
{
    private readonly string _configDir = Directory.CreateTempSubdirectory("cockpit-tty-launcher-tests-").FullName;
    private readonly IMcpServerCatalog _emptyMcpStore = CreateEmptyMcpCatalog();

    // An empty effective set, so _WriteRegistryMcpConfig produces no --mcp-config and the argument
    // assertions below stay about the start-default flags only.
    private static IMcpServerCatalog CreateEmptyMcpCatalog()
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersAsync().Returns(Task.FromResult<IReadOnlyList<McpServerConfig>>([]));
        return catalog;
    }

    [Fact]
    public void Launch_WithAProfile_PassesTheProfileConfigDirAndTermToThePtyHostFactory()
    {
        var executableLocator = Substitute.For<IClaudeExecutableLocator>();
        var ptyHostFactory = Substitute.For<IPtyHostFactory>();
        var expectedPty = Substitute.For<IConPtyProcess>();
        ptyHostFactory
            .Start(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<short>(), Arg.Any<short>())
            .Returns(expectedPty);

        var launcher = new ClaudeTtyLauncher(
            Options.Create(new CockpitOptions()),
            executableLocator,
            new WorkspaceTrustWriter(),
            ptyHostFactory,
            _emptyMcpStore);
        var profile = new SessionProfile("Personal", _configDir, ExecutablePath: "/usr/bin/claude");

        var pty = launcher.Launch(
            profile, permissionMode: "default", model: "sonnet", effort: "medium", columns: 120, rows: 40);

        pty.Should().BeSameAs(expectedPty);
        ptyHostFactory.Received(1).Start(
            "/usr/bin/claude",
            Arg.Any<IReadOnlyList<string>>(),
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
            .Start(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<short>(), Arg.Any<short>())
            .Returns(Substitute.For<IConPtyProcess>());

        var launcher = new ClaudeTtyLauncher(
            Options.Create(new CockpitOptions { Claude = new ClaudeCliOptions { WorkingDirectory = Directory.GetCurrentDirectory() } }),
            executableLocator,
            new WorkspaceTrustWriter(),
            ptyHostFactory,
            _emptyMcpStore);
        var profile = new SessionProfile("Personal", _configDir);

        launcher.Launch(profile, permissionMode: null, model: null, effort: null, columns: 80, rows: 24);

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
            .Start(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<short>(), Arg.Any<short>())
            .Returns(Substitute.For<IConPtyProcess>());

        var launcher = new ClaudeTtyLauncher(
            Options.Create(new CockpitOptions()),
            executableLocator,
            new WorkspaceTrustWriter(),
            ptyHostFactory,
            _emptyMcpStore);

        launcher.Launch(profile: null, permissionMode: null, model: null, effort: null, columns: 80, rows: 24);

        ptyHostFactory.Received(1).Start(
            "/opt/claude/claude",
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            80,
            24);
    }

    [Fact]
    public void Launch_WithOptions_PassesTheBuiltArgumentsToThePtyHostFactory()
    {
        var executableLocator = Substitute.For<IClaudeExecutableLocator>();
        var ptyHostFactory = Substitute.For<IPtyHostFactory>();
        ptyHostFactory
            .Start(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<short>(), Arg.Any<short>())
            .Returns(Substitute.For<IConPtyProcess>());

        var launcher = new ClaudeTtyLauncher(
            Options.Create(new CockpitOptions()),
            executableLocator,
            new WorkspaceTrustWriter(),
            ptyHostFactory,
            _emptyMcpStore);
        var profile = new SessionProfile("Personal", _configDir, ExecutablePath: "/usr/bin/claude");

        launcher.Launch(profile, permissionMode: "acceptEdits", model: "opus", effort: "xhigh", columns: 100, rows: 30);

        ptyHostFactory.Received(1).Start(
            "/usr/bin/claude",
            Arg.Is<IReadOnlyList<string>>(args => args.SequenceEqual(new[]
            {
                "--permission-mode", "acceptEdits", "--model", "opus", "--effort", "xhigh",
            })),
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            100,
            30);
    }

    [Fact]
    public void Launch_NeverForcesASessionId_SoTheCliPersistsATranscriptTheTailerCanFind()
    {
        var executableLocator = Substitute.For<IClaudeExecutableLocator>();
        var ptyHostFactory = Substitute.For<IPtyHostFactory>();
        ptyHostFactory
            .Start(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<short>(), Arg.Any<short>())
            .Returns(Substitute.For<IConPtyProcess>());

        var launcher = new ClaudeTtyLauncher(
            Options.Create(new CockpitOptions()),
            executableLocator,
            new WorkspaceTrustWriter(),
            ptyHostFactory,
            _emptyMcpStore);

        launcher.Launch(profile: null, permissionMode: null, model: null, effort: null, columns: 80, rows: 24);

        ptyHostFactory.Received(1).Start(
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<string>>(args => !args.Contains("--session-id")),
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            80,
            24);
    }

    public void Dispose() => Directory.Delete(_configDir, recursive: true);
}
