using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Provisioning;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Delegation;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Core.Tests.Provisioning;

/// <summary>
/// AC-1593: the organisation's permission ceilings hold on every route a session can take — the SDK start, the TTY
/// launch, a switch mid-session (typed or as the plain live option), wholesale auto-approve, and delegation.
/// </summary>
public class OrgPolicyCeilingTests
{
    private static readonly ProvisionPolicy Policy = new(
        "Acme",
        Permissions: new ProvisionPermissionPolicy(MaxPermissionMode: "acceptEdits", AutoApproveTools: OrgPolicyCeilings.Forbidden, DelegationCeiling: "acceptEdits"));

    [Theory]
    [InlineData("sdk-start")]
    [InlineData("sdk-start-sandbox")]
    [InlineData("sdk-start-blank")]
    [InlineData("tty-start")]
    [InlineData("switch")]
    [InlineData("live-option")]
    [InlineData("live-mode")]
    [InlineData("auto-approve")]
    public async Task NothingAboveTheCeilingRuns_OnAnyRoute(string route)
    {
        var bypass = new Dictionary<string, string> { [WellKnownPluginSessionOptions.PermissionMode] = "bypassPermissions" };
        if (route == "tty-start")
        {
            var provider = Substitute.For<ITtySessionProvider>();
            provider.BuildLaunch(Arg.Any<TtyLaunchContext>()).Returns(new TtyLaunchSpec("/usr/bin/cli", [], new Dictionary<string, string?>(), "/wd", []));
            var pty = Substitute.For<IPtyHostFactory>();
            pty.Start(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<short>(), Arg.Any<short>())
                .Returns(Substitute.For<IConPtyProcess>());
            var launcher = new TtyLauncher(pty, Substitute.For<ISessionMemoryLimiter>(), new McpAuthKey(), new SessionMcpKeyring(), NullLogger<TtyLauncher>.Instance, orgPolicy: _Org());
            string? notice = null;

            launcher.Launch(provider, profile: null, bypass, 80, 24, notice: message => notice = message);

            provider.Received(1).BuildLaunch(Arg.Is<TtyLaunchContext>(context => context.Options[WellKnownPluginSessionOptions.PermissionMode] == "acceptEdits"));
            Assert.Equal("Started in acceptEdits: Acme allows at most acceptEdits.", notice);
            return;
        }

        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        var runtime = new SessionRuntime(factory, profile: null, orgPolicy: _Org());
        var starts = new Dictionary<string, (Dictionary<string, string>? Options, string Key, string Expected)>
        {
            ["sdk-start"] = (bypass, WellKnownPluginSessionOptions.PermissionMode, "acceptEdits"),
            // Codex's "ask nothing" is its sandbox, not a mode; and no mode at all leaves the provider's own default.
            ["sdk-start-sandbox"] = (new() { ["sandbox"] = "danger-full-access" }, "sandbox", "workspace-write"),
            ["sdk-start-blank"] = (null, WellKnownPluginSessionOptions.PermissionMode, "default"),
        };
        await runtime.StartAsync(profile: null, launchOptions: starts.GetValueOrDefault(route).Options);

        switch (route)
        {
            case "sdk-start" or "sdk-start-sandbox" or "sdk-start-blank":
                var (_, key, expected) = starts[route];
                await driver.Received(1).StartAsync(null, Arg.Any<string>(), null, null, null, null,
                    Arg.Is<IReadOnlyDictionary<string, string>>(options => options[key] == expected), null, Arg.Any<CancellationToken>());
                break;
            case "switch":
                await Assert.ThrowsAsync<OrgPolicyRefusedException>(() => runtime.SetPermissionModeAsync("bypassPermissions"));
                await driver.DidNotReceive().SetPermissionModeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
                break;
            case "live-option":
                await Assert.ThrowsAsync<OrgPolicyRefusedException>(() => runtime.SetLiveOptionAsync("Permission-Mode", "bypassPermissions"));
                await driver.DidNotReceive().SetLiveOptionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
                break;
            case "live-mode":
                await Assert.ThrowsAsync<OrgPolicyRefusedException>(() => runtime.SetLiveOptionAsync("mode", "yolo"));
                await driver.DidNotReceive().SetLiveOptionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
                break;
            case "auto-approve":
                await runtime.SetAutoApproveToolsAsync(true);
                await driver.Received(1).SetAutoApproveToolsAsync(false, Arg.Any<CancellationToken>());
                break;
        }
    }

    // The request is clamped before consent is weighed, so nobody is asked a question whose yes would change nothing,
    // and an auto-approve profile still gets the ceiling installed rather than every call allowed.
    [Fact]
    public async Task ADelegatedRequestAboveTheCeiling_RunsAtTheCeiling_WithoutConsent_AndGated()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        var profile = new SessionProfile(
            "local",
            new ClaudeConfig(string.Empty),
            Defaults: new ProfileDefaults(string.Empty, string.Empty, string.Empty, AutoApproveTools: true),
            Delegation: new DelegationPolicy(AllowedAsTarget: true, PermissionCeiling: "bypassPermissions"));
        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([profile]);
        var mcpServerStore = Substitute.For<IMcpServerStore>();
        mcpServerStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([new McpServerConfig { Name = "filesystem", Enabled = true }]);
        var consent = Substitute.For<IConsentBroker>();
        consent.RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>()).Returns(new ConsentDecision(ConsentOutcome.Approved));
        var service = new DelegationService(profileStore, new SessionManager(factory, orgPolicy: _Org()), mcpServerStore, Substitute.For<IDelegationAuditLog>(),
            minutes => TimeSpan.FromMilliseconds(minutes * 30), consent: consent, orgPolicy: _Org());

        await service.DelegateAsync(new DelegationRequest("local", "do everything", RequestedPermission: "bypassPermissions"));

        await consent.DidNotReceive().RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>());
        await driver.Received(1).SetDelegatedToolGateAsync("acceptEdits", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await driver.DidNotReceive().SetAutoApproveToolsAsync(true, Arg.Any<CancellationToken>());
    }

    // The switch the operator flipped is kept, so lifting the ban gives back exactly what they chose. The session
    // tools that run a command answer to the shell ban too, though they never answered to the switch.
    [Theory]
    [InlineData("terminal")]
    [InlineData("shell")]
    [InlineData("start-run")]
    [InlineData("run-elevated")]
    [InlineData("unreadable")]
    public async Task AForbiddenAgentSwitch_StaysOff_UntilTheBanIsLifted(string which)
    {
        if (which == "unreadable")
        {
            // Neither the settings nor their backup parse: the switch reads as off rather than throwing into the fan-out.
            var directory = Directory.CreateTempSubdirectory("ac1593-").FullName;
            try
            {
                var path = Path.Combine(directory, "cockpit.json");
                await File.WriteAllTextAsync(path, "{ not json");
                await File.WriteAllTextAsync(path + ".bak", "{ not json");
                var terminal = new Cockpit.Infrastructure.Terminal.TerminalAccessState(new Cockpit.Infrastructure.Provisioning.OrgPolicy(path)) { Enabled = true };

                Assert.False(terminal.Enabled);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }

            return;
        }

        var org = Substitute.For<IOrgPolicy>();
        org.Current.Returns(new ProvisionPolicy("Acme", Permissions: new ProvisionPermissionPolicy(TerminalAccess: OrgPolicyCeilings.Forbidden, ShellAccess: OrgPolicyCeilings.Forbidden)));
        if (which is "start-run" or "run-elevated")
        {
            var runner = Substitute.For<ITrackedCommandRunner>();
            var tools = new SessionStatusTools(Substitute.For<ISessionLabelSink>(), runner, new RunTracker(), Substitute.For<IWorkspaceAgentGateway>(),
                Substitute.For<IWorkspaceAgentCoordinator>(), Substitute.For<IAgentMessageInbox>(), orgPolicy: org);
            var reply = which == "start-run"
                ? await tools.StartRunAsync(".", "dotnet", session: "pane")
                : await tools.RunElevatedAsync("Get-Date", ".", "test", session: "pane");

            Assert.Contains("Acme does not allow agents to run commands", reply);
            return;
        }

        var (read, write) = which == "terminal"
            ? _Switch(new Cockpit.Infrastructure.Terminal.TerminalAccessState(org))
            : _Switch(new Cockpit.Infrastructure.Shell.ShellAccessState(org));

        write(true);
        Assert.False(read());

        org.Current.Returns((ProvisionPolicy?)null);
        Assert.True(read());
    }

    private static (Func<bool> Read, Action<bool> Write) _Switch(ITerminalAccessSwitch terminal) => (() => terminal.Enabled, value => terminal.Enabled = value);

    private static (Func<bool> Read, Action<bool> Write) _Switch(IShellAccessSwitch shell) => (() => shell.Enabled, value => shell.Enabled = value);

    // Ollama and OpenRouter on listed models only: a start outside that does not run on either route, whichever of the
    // option, the typed model or the plugin's own config names the model, and a live switch away from the list is refused.
    [Theory]
    [InlineData("sdk-provider")]
    [InlineData("sdk-option-model")]
    [InlineData("sdk-config-model")]
    [InlineData("sdk-typed-model")]
    [InlineData("sdk-extra-args")]
    [InlineData("tty-provider")]
    [InlineData("tty-config-model")]
    [InlineData("live-model")]
    [InlineData("live-clear")]
    public async Task AProfileOutsideTheAllowedProviders_DoesNotStart(string route)
    {
        var rig = new ProviderRig();
        var acts = new Dictionary<string, Func<Task>>
        {
            ["sdk-provider"] = () => rig.Runtime.StartAsync(ProviderRig.Claude),
            ["sdk-option-model"] = () => rig.Runtime.StartAsync(ProviderRig.OpenRouter("{}"), launchOptions: ProviderRig.Model("o3")),
            ["sdk-config-model"] = () => rig.Runtime.StartAsync(ProviderRig.OpenRouter("""{ "Model": "o3" }""")),
            ["sdk-typed-model"] = () => rig.Runtime.StartAsync(ProviderRig.OpenRouter("{}"), model: "o3", launchOptions: ProviderRig.Model("gpt-5")),
            ["sdk-extra-args"] = () => rig.Runtime.StartAsync(ProviderRig.OpenRouter("""{ "extraArgs": ["-c", "model=\"o3\""] }""")),
            ["tty-provider"] = () => Task.FromResult(rig.Launch(rig.Provider("claude"), ProviderRig.Claude)),
            ["tty-config-model"] = () => Task.FromResult(rig.Launch(rig.Provider(ProviderRig.OpenRouterId), ProviderRig.OpenRouter("""{ "model": "o3" }"""))),
            ["live-model"] = async () =>
            {
                await rig.Runtime.StartAsync(ProviderRig.OpenRouter("{}"));
                await rig.Runtime.SetModelAsync("o3");
            },
            ["live-clear"] = async () =>
            {
                await rig.Runtime.StartAsync(ProviderRig.OpenRouter("{}"));
                await rig.Runtime.SetModelAsync(null);
            },
        };

        var refused = await Assert.ThrowsAsync<OrgPolicyRefusedException>(acts[route]);

        Assert.Contains("Acme", refused.Message);
        Assert.All(rig.StartedModels, model => Assert.Equal("gpt-5", model));
        await rig.Driver.DidNotReceive().SetModelAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
        rig.Pty.DidNotReceive().Start(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<short>(), Arg.Any<short>());
    }

    // A start that names no model runs on the first listed one rather than the provider's own default, on both routes and
    // in the listed spelling. The operator's own shell terminal runs no provider, so the provider list leaves it alone.
    [Theory]
    [InlineData("sdk", "gpt-5")]
    [InlineData("sdk-spelling", "gpt-5-mini")]
    [InlineData("tty", "gpt-5")]
    [InlineData("shell", null)]
    public async Task AStartWithinTheAllowedProviders_RunsOnAListedModel(string route, string? expected)
    {
        var rig = new ProviderRig();
        var acts = new Dictionary<string, Func<Task>>
        {
            ["sdk"] = () => rig.Runtime.StartAsync(ProviderRig.OpenRouter("{}")),
            ["sdk-spelling"] = () => rig.Runtime.StartAsync(ProviderRig.OpenRouter("{}"), launchOptions: ProviderRig.Model("GPT-5-Mini")),
            ["tty"] = () => Task.FromResult(rig.Launch(rig.Provider(ProviderRig.OpenRouterId), ProviderRig.OpenRouter("{}"))),
            ["shell"] = () => Task.FromResult(rig.Launch(new Cockpit.Core.Terminal.ShellTtySessionProvider(new("bash", "Bash", "/bin/bash", [])), profile: null)),
        };

        await acts[route]();

        Assert.Equal(expected, rig.LaunchedOptions?.GetValueOrDefault(WellKnownPluginSessionOptions.Model));
        Assert.All(rig.StartedModels, model => Assert.Equal(expected, model));
    }

    private sealed class ProviderRig
    {
        public const string OpenRouterId = "openrouter-provider.openrouter";

        public static readonly SessionProfile Claude = new("claude", ClaudePluginProfile.Create(string.Empty, null));

        private readonly TtyLauncher _launcher;

        public ProviderRig()
        {
            var org = Substitute.For<IOrgPolicy>();
            org.ReadAsync(Arg.Any<CancellationToken>()).Returns(new ProvisionPolicy("Acme", Providers: new ProvisionProviderPolicy(
            [
                new ProvisionAllowedProvider("ollama", System.Text.Json.JsonSerializer.SerializeToElement(new[] { "qwen3" })),
                new ProvisionAllowedProvider(OpenRouterId, System.Text.Json.JsonSerializer.SerializeToElement(new[] { "gpt-5", "gpt-5-mini" })),
            ])));
            Driver.Events.Returns(_EmptyStream());
            Driver.StartAsync(Arg.Any<SessionProfile?>(), Arg.Any<string?>(), Arg.Do<string?>(model => StartedModels.Add(model)), Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(),
                    Arg.Any<SessionResume?>(), Arg.Do<IReadOnlyDictionary<string, string>?>(options => LaunchedOptions = options), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            var factory = Substitute.For<ISessionDriverFactory>();
            factory.Create(Arg.Any<SessionProfile?>()).Returns(Driver);
            Runtime = new SessionRuntime(factory, profile: null, orgPolicy: org);
            Pty.Start(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<short>(), Arg.Any<short>())
                .Returns(Substitute.For<IConPtyProcess>());
            _launcher = new TtyLauncher(Pty, Substitute.For<ISessionMemoryLimiter>(), new McpAuthKey(), new SessionMcpKeyring(), NullLogger<TtyLauncher>.Instance, orgPolicy: org);
        }

        public ISessionDriver Driver { get; } = Substitute.For<ISessionDriver>();

        public IPtyHostFactory Pty { get; } = Substitute.For<IPtyHostFactory>();

        public SessionRuntime Runtime { get; }

        public List<string?> StartedModels { get; } = [];

        public IReadOnlyDictionary<string, string>? LaunchedOptions { get; private set; }

        public static SessionProfile OpenRouter(string configJson) => new("router", new PluginProviderConfig(OpenRouterId, configJson));

        public static Dictionary<string, string> Model(string model) => new() { [WellKnownPluginSessionOptions.Model] = model };

        public ITtySessionProvider Provider(string providerId)
        {
            var provider = Substitute.For<ITtySessionProvider>();
            provider.ProviderId.Returns(providerId);
            provider.BuildLaunch(Arg.Do<TtyLaunchContext>(context => LaunchedOptions = context.Options))
                .Returns(new TtyLaunchSpec("/usr/bin/cli", [], new Dictionary<string, string?>(), "/wd", []));
            return provider;
        }

        public IConPtyProcess Launch(ITtySessionProvider provider, SessionProfile? profile) =>
            _launcher.Launch(provider, profile, new Dictionary<string, string>(), 80, 24);
    }

    private static IOrgPolicy _Org()
    {
        var org = Substitute.For<IOrgPolicy>();
        org.ReadAsync(Arg.Any<CancellationToken>()).Returns(Policy);
        return org;
    }

    private static async IAsyncEnumerable<SessionEvent> _EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }
}
