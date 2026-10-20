using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
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
