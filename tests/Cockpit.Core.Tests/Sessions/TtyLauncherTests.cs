using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Mcp;
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
        var (launcher, ptyHostFactory, _) = CreateLauncherWithKey(logger);
        return (launcher, ptyHostFactory);
    }

    private static (TtyLauncher Launcher, IPtyHostFactory PtyHostFactory, McpAuthKey AuthKey) CreateLauncherWithKey(ILogger<TtyLauncher>? logger = null)
    {
        var (launcher, ptyHostFactory, authKey, _) = CreateLauncherWithKeyring(logger);
        return (launcher, ptyHostFactory, authKey);
    }

    private static (TtyLauncher Launcher, IPtyHostFactory PtyHostFactory, McpAuthKey AuthKey, SessionMcpKeyring Keyring) CreateLauncherWithKeyring(ILogger<TtyLauncher>? logger = null)
    {
        var ptyHostFactory = Substitute.For<IPtyHostFactory>();
        ptyHostFactory
            .Start(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<short>(), Arg.Any<short>())
            .Returns(Substitute.For<IConPtyProcess>());
        var authKey = new McpAuthKey();
        var keyring = new SessionMcpKeyring();
        // No cap for the tests that are not about one — a limiter that enforces nothing is what macOS really hands
        // back, and it keeps the launcher's own wrapping decisions readable here.
        var limiter = Substitute.For<ISessionMemoryLimiter>();
        limiter.Apply(Arg.Any<int>(), Arg.Any<long>()).Returns((IDisposable?)null);
        return (new TtyLauncher(ptyHostFactory, limiter, authKey, keyring, logger ?? NullLogger<TtyLauncher>.Instance), ptyHostFactory, authKey, keyring);
    }

    // AC-40 regression: the cockpit-hosted MCP endpoints (cockpit-session/-orchestrator/-workflows) 401 without
    // this run's key, and a TTY child (Claude's Bearer ${COCKPIT_MCP_KEY}, Codex's bearer_token_env_var) only ever
    // presents it if the host sets the env var on the child. The host owns it on the base — reaching both the
    // provider's view and the spawned child — rather than an overlay the scrub would drop.
    [Fact]
    public void Launch_TheEnvironmentPassedToThePtyHost_CarriesThisRunsMcpAuthKey()
    {
        var (launcher, ptyHostFactory, authKey) = CreateLauncherWithKey();
        var spec = new TtyLaunchSpec("/usr/bin/cli", [], new Dictionary<string, string?>(), "/wd", []);
        var provider = Provider(spec);

        launcher.Launch(provider, profile: null, options: new Dictionary<string, string>(), columns: 80, rows: 24);

        provider.Received(1).BuildLaunch(Arg.Is<TtyLaunchContext>(context =>
            context.BaseEnvironment["COCKPIT_MCP_KEY"] == authKey.Value));
        ptyHostFactory.Received(1).Start(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(env => env["COCKPIT_MCP_KEY"] == authKey.Value),
            80,
            24);
    }

    // A profile or provider that set COCKPIT_MCP_KEY to a value of its own would present the wrong key and lock the
    // session out with a self-inflicted 401 — so the key is host-controlled and the host's own value must win.
    [Fact]
    public void Launch_AProviderOverlayThatTriesToOverrideTheMcpAuthKey_CannotChangeIt()
    {
        var (launcher, ptyHostFactory, authKey) = CreateLauncherWithKey();
        var spec = new TtyLaunchSpec(
            "/usr/bin/cli",
            [],
            new Dictionary<string, string?> { ["COCKPIT_MCP_KEY"] = "forged-by-the-provider" },
            "/wd",
            []);
        var provider = Provider(spec);

        launcher.Launch(provider, profile: null, options: new Dictionary<string, string>(), columns: 80, rows: 24);

        ptyHostFactory.Received(1).Start(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(env => env["COCKPIT_MCP_KEY"] == authKey.Value),
            80,
            24);
    }

    [Fact]
    public void Launch_AProfileVariableOnTheMcpAuthKey_CannotOverrideIt()
    {
        var (launcher, ptyHostFactory, authKey) = CreateLauncherWithKey();
        var spec = new TtyLaunchSpec("/usr/bin/cli", [], new Dictionary<string, string?>(), "/wd", []);
        var provider = Provider(spec);
        var profile = new SessionProfile("work", new ClaudeConfig("/config/dir"))
        {
            EnvironmentVariables = [new ProfileEnvironmentVariable("COCKPIT_MCP_KEY", "forged-by-the-operator")],
        };

        launcher.Launch(provider, profile, options: new Dictionary<string, string>(), columns: 80, rows: 24);

        ptyHostFactory.Received(1).Start(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(env => env["COCKPIT_MCP_KEY"] == authKey.Value),
            80,
            24);
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

    // AC-143: TtyLauncher is one of the two remaining mint sites the SessionMcpKeyring class doc used to flag as
    // "not yet covered" — the pty's own end is invisible to the app layer that could otherwise call Revoke, so the
    // returned process itself has to carry the teardown. This is the mutation-style guard: it asserts the keyring's
    // own live-entry count went from minted to revoked, not just that Dispose ran without throwing, so deleting the
    // Revoke call in TtyProcessOwningSessionFiles.Dispose turns this red.
    [Fact]
    public void Launch_WithAPaneId_MintsAKeyringTokenThatIsRevokedWhenTheReturnedProcessIsDisposed()
    {
        var (launcher, _, _, keyring) = CreateLauncherWithKeyring();
        var spec = new TtyLaunchSpec("/usr/bin/cli", [], new Dictionary<string, string?>(), "/wd", []);
        var provider = Provider(spec);

        var process = launcher.Launch(provider, profile: null, options: new Dictionary<string, string>(), columns: 80, rows: 24, paneId: "tty-pane-under-test");

        Assert.Equal(1, keyring.LivePaneCount);

        process.Dispose();

        Assert.Equal(0, keyring.LivePaneCount);
        Assert.Equal(0, keyring.LiveTokenCount);
    }

    // AC-1148: the property the whole authorization gate rests on. `COCKPIT_MCP_KEY` is what a session can read out
    // of its own environment, and it has to be the token the keyring minted for this pane — the app key names no
    // session, so a session holding that one would be authorized as the cockpit itself on every endpoint.
    [Fact]
    public void Launch_WithAPaneId_PutsThatPanesKeyringTokenInTheEnvironment_NeverTheSharedAppKey()
    {
        var (launcher, ptyHostFactory, authKey, keyring) = CreateLauncherWithKeyring();
        var provider = Provider(new TtyLaunchSpec("/usr/bin/cli", [], new Dictionary<string, string?>(), "/wd", []));

        launcher.Launch(provider, profile: null, options: new Dictionary<string, string>(), columns: 80, rows: 24, paneId: "tty-pane-under-test");

        ptyHostFactory.Received().Start(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(env =>
                keyring.PaneFor(env["COCKPIT_MCP_KEY"]) == "tty-pane-under-test"
                && env["COCKPIT_MCP_KEY"] != authKey.Value),
            Arg.Any<short>(),
            Arg.Any<short>());
    }

    // Iron Law #8 (no secret in a log/error message): the pane id is safe to log (it is not the secret), but the
    // minted bearer token itself must never appear in a log line anywhere on this teardown path.
    [Fact]
    public void Launch_WithAPaneId_TheMintedTokenNeverAppearsInALogMessage()
    {
        var logger = Substitute.For<ILogger<TtyLauncher>>();
        var (launcher, _, _, _) = CreateLauncherWithKeyring(logger);
        var spec = new TtyLaunchSpec("/usr/bin/cli", [], new Dictionary<string, string?>(), "/wd", []);
        var provider = Provider(spec);

        var process = launcher.Launch(provider, profile: null, options: new Dictionary<string, string>(), columns: 80, rows: 24, paneId: "tty-pane-under-test");
        process.Dispose();

        // The keyring mints a 64-character hex string (32 random bytes); no log call's rendered message anywhere
        // in this path may contain a substring shaped like one.
        Assert.DoesNotContain(logger.ReceivedCalls(), call =>
            call.GetArguments().OfType<object>().Any(argument => _ContainsAHexToken(argument == null ? null : argument.ToString())));
    }

    /// <summary>Whether <paramref name="text"/> contains a run of 64 hex characters — the shape of a minted keyring token.</summary>
    private static bool _ContainsAHexToken(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var run = 0;
        foreach (var character in text)
        {
            run = Uri.IsHexDigit(character) ? run + 1 : 0;
            if (run >= 64)
            {
                return true;
            }
        }

        return false;
    }
}
