using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// Covers <see cref="TtyLauncher"/>'s own job, now that the Claude-specific pieces moved out to
/// <c>ITtySessionProvider</c>: it hands the provider a <see cref="TtyLaunchContext"/>, composes the host's
/// base environment with whatever overlay the provider returns, spawns exactly that through
/// <see cref="IPtyHostFactory"/>, and wraps the result so disposing it cleans up the provider's
/// session-scoped files. All against a substituted <see cref="ITtySessionProvider"/> — provider-neutral,
/// same as the launcher itself.
/// </summary>
public class TtyLauncherTests
{
    private static ITtySessionProvider Provider(TtyLaunchSpec spec)
    {
        var provider = Substitute.For<ITtySessionProvider>();
        provider.ProviderId.Returns("test-provider");
        provider.BuildLaunch(Arg.Any<TtyLaunchContext>()).Returns(spec);
        return provider;
    }

    private static (TtyLauncher Launcher, IPtyHostFactory PtyHostFactory) CreateLauncher(ILogger<TtyLauncher>? logger = null)
    {
        var ptyHostFactory = Substitute.For<IPtyHostFactory>();
        ptyHostFactory
            .Start(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<short>(), Arg.Any<short>())
            .Returns(Substitute.For<IConPtyProcess>());
        return (new TtyLauncher(ptyHostFactory, logger ?? NullLogger<TtyLauncher>.Instance), ptyHostFactory);
    }

    [Fact]
    public void Launch_PassesTheSpecsExecutableArgumentsAndWorkingDirectoryToThePtyHostFactory()
    {
        var (launcher, ptyHostFactory) = CreateLauncher();
        var spec = new TtyLaunchSpec(
            "/usr/bin/some-cli",
            ["--flag", "value"],
            new Dictionary<string, string?>(),
            "/some/working/dir",
            []);
        var provider = Provider(spec);

        launcher.Launch(provider, profile: null, options: new Dictionary<string, string>(), columns: 100, rows: 30);

        ptyHostFactory.Received(1).Start(
            "/usr/bin/some-cli",
            Arg.Is<IReadOnlyList<string>>(args => args.SequenceEqual(new[] { "--flag", "value" })),
            "/some/working/dir",
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            100,
            30);
    }

    [Fact]
    public void Launch_ReturnsExactlyWhatThePtyHostFactoryReturned_WhenTheSpecHasNoFilesToOwn()
    {
        var (launcher, ptyHostFactory) = CreateLauncher();
        var expectedProcess = Substitute.For<IConPtyProcess>();
        ptyHostFactory
            .Start(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<short>(), Arg.Any<short>())
            .Returns(expectedProcess);
        var spec = new TtyLaunchSpec("/usr/bin/cli", [], new Dictionary<string, string?>(), "/wd", []);
        var provider = Provider(spec);

        var process = launcher.Launch(provider, profile: null, options: new Dictionary<string, string>(), columns: 80, rows: 24);

        process.Should().BeSameAs(expectedProcess);
    }

    [Fact]
    public void Launch_PassesTheProfileOptionsWorkingDirectoryAndResumeThroughToTheProvider()
    {
        var (launcher, _) = CreateLauncher();
        var spec = new TtyLaunchSpec("/usr/bin/cli", [], new Dictionary<string, string?>(), "/wd", []);
        var provider = Provider(spec);
        var profile = new SessionProfile("work", new ClaudeConfig("/config/dir"));
        var options = new Dictionary<string, string> { ["model"] = "opus" };
        var resume = SessionResume.MostRecent;

        launcher.Launch(provider, profile, options, columns: 80, rows: 24, workingDirectory: "/explicit/dir", resume: resume);

        provider.Received(1).BuildLaunch(Arg.Is<TtyLaunchContext>(context =>
            context.Profile == profile
            && context.Options == options
            && context.WorkingDirectory == Path.GetFullPath("/explicit/dir")
            && context.Resume == resume));
    }

    [Fact]
    public void Launch_WithoutAnExplicitWorkingDirectory_UsesTheProcessCurrentDirectory()
    {
        var (launcher, _) = CreateLauncher();
        var spec = new TtyLaunchSpec("/usr/bin/cli", [], new Dictionary<string, string?>(), "/wd", []);
        var provider = Provider(spec);

        launcher.Launch(provider, profile: null, options: new Dictionary<string, string>(), columns: 80, rows: 24);

        provider.Received(1).BuildLaunch(Arg.Is<TtyLaunchContext>(context =>
            context.WorkingDirectory == Path.GetFullPath(Directory.GetCurrentDirectory())));
    }

    [Fact]
    public void Launch_TheEnvironmentPassedToThePtyHost_IncludesBothTheHostBaseAndTheProvidersOverlay()
    {
        var (launcher, ptyHostFactory) = CreateLauncher();
        var spec = new TtyLaunchSpec(
            "/usr/bin/cli",
            [],
            new Dictionary<string, string?> { ["PROVIDER_ONLY_VAR"] = "from-the-provider" },
            "/wd",
            []);
        var provider = Provider(spec);

        launcher.Launch(provider, profile: null, options: new Dictionary<string, string>(), columns: 80, rows: 24);

        ptyHostFactory.Received(1).Start(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(env =>
                env["TERM"] == "xterm-256color" && env["PROVIDER_ONLY_VAR"] == "from-the-provider"),
            80,
            24);
    }

    [Fact]
    public void Launch_WhenTheOverlaySetsAnInheritedVariableToNull_RemovesItFromWhatThePtyHostReceives()
    {
        const string variable = "COCKPIT_TTY_LAUNCHER_TEST_VAR";
        Environment.SetEnvironmentVariable(variable, "inherited-from-the-shell");
        try
        {
            var (launcher, ptyHostFactory) = CreateLauncher();
            var spec = new TtyLaunchSpec(
                "/usr/bin/cli",
                [],
                new Dictionary<string, string?> { [variable] = null },
                "/wd",
                []);
            var provider = Provider(spec);

            launcher.Launch(provider, profile: null, options: new Dictionary<string, string>(), columns: 80, rows: 24);

            ptyHostFactory.Received(1).Start(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(),
                Arg.Is<IReadOnlyDictionary<string, string>>(env => !env.ContainsKey(variable)),
                80,
                24);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    // A provider cannot reinstate what the host stripped: TtyEnvironment.Compose ignores an overlay entry
    // for a host-controlled key (IsHostControlled — the nested-agent markers, the host terminal identity,
    // any ANTHROPIC_* credential) unless it removes it. A provider that tries anyway is not silently
    // obeyed and not silently ignored either — TtyLauncher logs a warning naming the rejected keys (never
    // the values, which are the secret) so the attempt is visible instead of a session quietly landing on
    // API-key billing.
    [Fact]
    public void Launch_AProviderOverlayThatTriesToSetAnAnthropicCredential_NeverReachesThePtyHost()
    {
        const string variable = "ANTHROPIC_API_KEY";
        Environment.SetEnvironmentVariable(variable, null);
        try
        {
            var (launcher, ptyHostFactory) = CreateLauncher();
            var spec = new TtyLaunchSpec(
                "/usr/bin/cli",
                [],
                new Dictionary<string, string?> { [variable] = "set-deliberately-by-the-provider" },
                "/wd",
                []);
            var provider = Provider(spec);

            launcher.Launch(provider, profile: null, options: new Dictionary<string, string>(), columns: 80, rows: 24);

            ptyHostFactory.Received(1).Start(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(),
                Arg.Is<IReadOnlyDictionary<string, string>>(env => !env.ContainsKey(variable)),
                80,
                24);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void Launch_AProviderOverlayThatTriesToSetAHostControlledKey_LogsAWarningNamingIt()
    {
        var logger = Substitute.For<ILogger<TtyLauncher>>();
        var (launcher, _) = CreateLauncher(logger);
        var spec = new TtyLaunchSpec(
            "/usr/bin/cli",
            [],
            new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = "set-deliberately-by-the-provider" },
            "/wd",
            []);
        var provider = Provider(spec);

        launcher.Launch(provider, profile: null, options: new Dictionary<string, string>(), columns: 80, rows: 24);

        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state!.ToString()!.Contains("ANTHROPIC_API_KEY")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void Launch_WithoutAnyInheritedAnthropicCredential_TheProvidersOverlayStillCannotIntroduceOne()
    {
        const string variable = "ANTHROPIC_API_KEY";
        Environment.SetEnvironmentVariable(variable, "inherited-from-the-shell");
        try
        {
            var (launcher, ptyHostFactory) = CreateLauncher();
            var spec = new TtyLaunchSpec("/usr/bin/cli", [], new Dictionary<string, string?>(), "/wd", []);
            var provider = Provider(spec);

            launcher.Launch(provider, profile: null, options: new Dictionary<string, string>(), columns: 80, rows: 24);

            ptyHostFactory.Received(1).Start(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(),
                Arg.Is<IReadOnlyDictionary<string, string>>(env => !env.ContainsKey(variable)),
                80,
                24);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    // The profile's own variables (AC-22) sit between the inherited base and the provider's overlay: an
    // operator value overrides what the cockpit inherited, the provider keeps the last word, and the
    // host-controlled scrub applies to the operator exactly as it does to a provider.
    [Fact]
    public void Launch_AProfileEnvironmentVariable_ReachesThePtyHostAndTheProvidersBaseEnvironment()
    {
        var (launcher, ptyHostFactory) = CreateLauncher();
        var spec = new TtyLaunchSpec("/usr/bin/cli", [], new Dictionary<string, string?>(), "/wd", []);
        var provider = Provider(spec);
        var profile = new SessionProfile("work", new ClaudeConfig("/config/dir"))
        {
            EnvironmentVariables = [new ProfileEnvironmentVariable("AI_OS_ROOT", "/home/raymond/AI-OS")],
        };

        launcher.Launch(provider, profile, options: new Dictionary<string, string>(), columns: 80, rows: 24);

        provider.Received(1).BuildLaunch(Arg.Is<TtyLaunchContext>(context =>
            context.BaseEnvironment["AI_OS_ROOT"] == "/home/raymond/AI-OS"));
        ptyHostFactory.Received(1).Start(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(env => env["AI_OS_ROOT"] == "/home/raymond/AI-OS"),
            80,
            24);
    }

    [Fact]
    public void Launch_WhenTheProfileAndTheProviderSetTheSameVariable_TheProvidersOverlayWins()
    {
        var (launcher, ptyHostFactory) = CreateLauncher();
        var spec = new TtyLaunchSpec(
            "/usr/bin/cli",
            [],
            new Dictionary<string, string?> { ["SHARED_VAR"] = "from-the-provider" },
            "/wd",
            []);
        var provider = Provider(spec);
        var profile = new SessionProfile("work", new ClaudeConfig("/config/dir"))
        {
            EnvironmentVariables = [new ProfileEnvironmentVariable("SHARED_VAR", "from-the-profile")],
        };

        launcher.Launch(provider, profile, options: new Dictionary<string, string>(), columns: 80, rows: 24);

        ptyHostFactory.Received(1).Start(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(env => env["SHARED_VAR"] == "from-the-provider"),
            80,
            24);
    }

    [Fact]
    public void Launch_AProfileVariableOnAHostControlledKey_NeverReachesThePtyHostAndIsLoggedByName()
    {
        const string variable = "ANTHROPIC_API_KEY";
        Environment.SetEnvironmentVariable(variable, null);
        try
        {
            var logger = Substitute.For<ILogger<TtyLauncher>>();
            var (launcher, ptyHostFactory) = CreateLauncher(logger);
            var spec = new TtyLaunchSpec("/usr/bin/cli", [], new Dictionary<string, string?>(), "/wd", []);
            var provider = Provider(spec);
            var profile = new SessionProfile("work", new ClaudeConfig("/config/dir"))
            {
                EnvironmentVariables = [new ProfileEnvironmentVariable(variable, "set-by-the-operator", IsSecret: true)],
            };

            launcher.Launch(provider, profile, options: new Dictionary<string, string>(), columns: 80, rows: 24);

            ptyHostFactory.Received(1).Start(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(),
                Arg.Is<IReadOnlyDictionary<string, string>>(env => !env.ContainsKey(variable)),
                80,
                24);
            logger.Received(1).Log(
                LogLevel.Warning,
                Arg.Any<EventId>(),
                Arg.Is<object>(state => state!.ToString()!.Contains(variable) && !state.ToString()!.Contains("set-by-the-operator")),
                null,
                Arg.Any<Func<object, Exception?, string>>());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void Launch_WithSessionScopedFilesAndAStatusFile_DeletesThemWhenTheReturnedProcessIsDisposed()
    {
        var directory = Directory.CreateTempSubdirectory("cockpit-tty-launcher-tests-").FullName;
        try
        {
            var sessionFile = Path.Combine(directory, "mcp-config.json");
            var statusFile = Path.Combine(directory, "status.json");
            File.WriteAllText(sessionFile, "{}");
            File.WriteAllText(statusFile, "{}");

            var (launcher, ptyHostFactory) = CreateLauncher();
            var innerProcess = Substitute.For<IConPtyProcess>();
            ptyHostFactory
                .Start(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<short>(), Arg.Any<short>())
                .Returns(innerProcess);
            var spec = new TtyLaunchSpec("/usr/bin/cli", [], new Dictionary<string, string?>(), "/wd", [sessionFile], statusFile);
            var provider = Provider(spec);

            var process = launcher.Launch(provider, profile: null, options: new Dictionary<string, string>(), columns: 80, rows: 24);

            File.Exists(sessionFile).Should().BeTrue("the CLI reads it while the session is alive");
            File.Exists(statusFile).Should().BeTrue("the header polls it while the session is alive");

            process.Dispose();

            innerProcess.Received(1).Dispose();
            File.Exists(sessionFile).Should().BeFalse("a credential must not outlive the session that needed it");
            File.Exists(statusFile).Should().BeFalse("the limits of a session that has ended are nobody's business");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
