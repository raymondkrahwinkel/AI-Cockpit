using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The New-session dialog's logic (#31/#17/#15): loading profiles, initialising the start options from
/// the chosen profile's saved defaults, gating Start on a logged-in profile, and returning the
/// confirmed choices via <see cref="NewSessionDialogViewModel.CloseRequested"/>.
/// </summary>
public class NewSessionDialogViewModelTests
{
    [Fact]
    public async Task LoadAsync_PopulatesProfilesAndSelectsTheFirst()
    {
        var work = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var personal = new SessionProfile("personal", new ClaudeConfig("/home/r/.claude-personal"));
        var vm = NewVm(out _, work, personal);

        await vm.LoadAsync();

        vm.Profiles.Should().Equal(work, personal);
        vm.SelectedProfile.Should().Be(work);
    }

    [Fact]
    public async Task SelectingProfile_LoadsItsSavedDefaults()
    {
        var profile = new SessionProfile(
            "work",
            new ClaudeConfig("/home/r/.claude-work"),
            Defaults: new ProfileDefaults("bypassPermissions", "opus", "high"));
        var vm = NewVm(out _, profile);
        await vm.LoadAsync();

        vm.SelectedPermissionMode.Value.Should().Be("bypassPermissions");
        vm.SelectedModel.Value.Should().Be("opus");
        vm.SelectedEffort.Value.Should().Be("high");
    }

    [Fact]
    public async Task SelectingProfileWithoutDefaults_FallsBackToTheAppDefaults()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var vm = NewVm(out _, profile);
        await vm.LoadAsync();

        vm.SelectedPermissionMode.Should().Be(SessionOptionCatalog.DefaultPermissionMode);
        vm.SelectedModel.Should().Be(SessionOptionCatalog.DefaultModel);
        vm.SelectedEffort.Should().Be(SessionOptionCatalog.DefaultEffort);
    }

    [Fact]
    public async Task CanStart_IsFalseForSdkWhenTheSelectedProfileIsNotLoggedIn()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var vm = NewVm(out var loginChecker, profile);
        loginChecker.IsLoggedIn(profile).Returns(false);

        await vm.LoadAsync();
        vm.SelectSdkCommand.Execute(null);

        vm.IsSelectedProfileLoggedIn.Should().BeFalse();
        vm.CanStart.Should().BeFalse();
        vm.ConfirmCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task CanStart_IsTrueForTtyEvenWhenNotLoggedIn_SinceTheTuiRunsItsOwnLogin()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var vm = NewVm(out var loginChecker, profile);
        loginChecker.IsLoggedIn(profile).Returns(false);

        await vm.LoadAsync(); // the default kind is TTY

        vm.IsTty.Should().BeTrue();
        vm.IsSelectedProfileLoggedIn.Should().BeFalse();
        vm.CanStart.Should().BeTrue();
        vm.ConfirmCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task ShowLoginHint_IsShownForSdkButNotForTty_WhichLogsInItself()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var vm = NewVm(out var loginChecker, profile);
        loginChecker.IsLoggedIn(profile).Returns(false);
        await vm.LoadAsync();

        vm.SelectSdkCommand.Execute(null);
        vm.ShowLoginHint.Should().BeTrue();

        vm.SelectTtyCommand.Execute(null);
        vm.ShowLoginHint.Should().BeFalse();
    }

    [Fact]
    public async Task SelectingLocalProfile_IsStartableWithoutLogin_AndHidesClaudeOptions()
    {
        var local = new SessionProfile(
            "ollama",
            new OllamaConfig("http://localhost:11434", "llama3.1"));
        var vm = NewVm(out var loginChecker, local);
        loginChecker.IsLoggedIn(local).Returns(false); // a local provider has no login
        await vm.LoadAsync();

        vm.IsLocalProfile.Should().BeTrue();
        vm.CanStart.Should().BeTrue();
        vm.ShowSessionOptions.Should().BeFalse();
        vm.SelectedProviderLabel.Should().Be("Ollama");
    }

    [Fact]
    public async Task SelectingLocalProfile_ForcesSdkKind()
    {
        var local = new SessionProfile(
            "ollama",
            new OllamaConfig("http://localhost:11434", "llama3.1"));
        var vm = NewVm(out _, local);
        vm.SelectTtyCommand.Execute(null);

        await vm.LoadAsync();

        vm.SelectedKind.Should().Be(SessionKind.Sdk);
    }

    [Fact]
    public async Task Confirm_RaisesCloseWithTheChosenProfileAndOptions()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var vm = NewVm(out var loginChecker, profile);
        loginChecker.IsLoggedIn(profile).Returns(true);
        await vm.LoadAsync();
        vm.SelectedModel = new ModelOption("Haiku", "haiku");

        NewSessionResult? result = null;
        var closed = false;
        vm.CloseRequested += r => { result = r; closed = true; };

        vm.ConfirmCommand.Execute(null);

        closed.Should().BeTrue();
        result.Should().NotBeNull();
        result!.Profile.Should().Be(profile);
        result.Model.Value.Should().Be("haiku");
    }

    [Fact]
    public void Cancel_RaisesCloseWithNull()
    {
        var vm = NewVm(out _);
        NewSessionResult? result = new(SessionKind.Sdk, new SessionProfile("x", new ClaudeConfig("y")), SessionOptionCatalog.DefaultPermissionMode,
            SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort, null);
        var closed = false;
        vm.CloseRequested += r => { result = r; closed = true; };

        vm.CancelCommand.Execute(null);

        closed.Should().BeTrue();
        result.Should().BeNull();
    }

    [Fact]
    public void DefaultKind_IsTty_TheOperatorsPreferredDefault()
    {
        var vm = NewVm(out _);

        vm.SelectedKind.Should().Be(SessionKind.Tty);
        vm.IsTty.Should().BeTrue();
        vm.IsSdk.Should().BeFalse();
    }

    [Fact]
    public void SelectTty_SwitchesIsSdkAndIsTty()
    {
        var vm = NewVm(out _);

        vm.SelectTtyCommand.Execute(null);

        vm.SelectedKind.Should().Be(SessionKind.Tty);
        vm.IsSdk.Should().BeFalse();
        vm.IsTty.Should().BeTrue();

        vm.SelectSdkCommand.Execute(null);

        vm.SelectedKind.Should().Be(SessionKind.Sdk);
        vm.IsSdk.Should().BeTrue();
        vm.IsTty.Should().BeFalse();
    }

    [Fact]
    public void SdkAndTtyKind_BothShowSessionOptions_SinceTtyNowPassesThemAsLaunchOnlyStartDefaults()
    {
        var vm = NewVm(out _);

        vm.ShowSessionOptions.Should().BeTrue();
        vm.SelectTtyCommand.Execute(null);
        vm.ShowSessionOptions.Should().BeTrue();
    }

    [Fact]
    public async Task Confirm_CarriesTheSelectedKind()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var vm = NewVm(out var loginChecker, profile);
        loginChecker.IsLoggedIn(profile).Returns(true);
        await vm.LoadAsync();
        vm.SelectTtyCommand.Execute(null);

        NewSessionResult? result = null;
        vm.CloseRequested += r => result = r;

        vm.ConfirmCommand.Execute(null);

        result.Should().NotBeNull();
        result!.Kind.Should().Be(SessionKind.Tty);
    }

    [Fact]
    public void PermissionModes_IncludeBypass_SinceTheDialogIsWhereItCanBeChosen()
    {
        var vm = NewVm(out _);

        vm.PermissionModes.Select(mode => mode.Value).Should().Contain("bypassPermissions");
    }

    [Fact]
    public async Task LoadAsync_PopulatesTheMcpChecklist_AllCheckedByDefault()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var vm = NewVmWithMcp(out _, [profile],
            new McpServerConfig { Name = "server-a" },
            new McpServerConfig { Name = "server-b" });

        await vm.LoadAsync();

        vm.HasMcpServers.Should().BeTrue();
        vm.McpServers.Select(server => server.Name).Should().Equal("server-a", "server-b");
        vm.McpServers.Should().OnlyContain(server => server.IsEnabledForSession);
    }

    [Fact]
    public async Task LoadAsync_ExcludesDisabledRegistryServers_FromTheChecklist()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var vm = NewVmWithMcp(out _, [profile],
            new McpServerConfig { Name = "on" },
            new McpServerConfig { Name = "off", Enabled = false });

        await vm.LoadAsync();

        vm.McpServers.Select(server => server.Name).Should().Equal("on");
    }

    [Fact]
    public async Task LoadAsync_WithNoRegistryServers_HasMcpServersIsFalse()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var vm = NewVmWithMcp(out _, [profile]);

        await vm.LoadAsync();

        vm.HasMcpServers.Should().BeFalse();
        vm.McpServers.Should().BeEmpty();
    }

    [Fact]
    public async Task Confirm_WithAnUncheckedMcpServer_ExcludesItFromTheResult()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var vm = NewVmWithMcp(out var loginChecker, [profile],
            new McpServerConfig { Name = "server-a" },
            new McpServerConfig { Name = "server-b" });
        loginChecker.IsLoggedIn(profile).Returns(true);
        await vm.LoadAsync();

        vm.McpServers.Single(server => server.Name == "server-b").IsEnabledForSession = false;

        NewSessionResult? result = null;
        vm.CloseRequested += r => result = r;
        vm.ConfirmCommand.Execute(null);

        result.Should().NotBeNull();
        result!.EnabledMcpServerNames.Should().BeEquivalentTo(["server-a"]);
    }

    [Fact]
    public async Task Confirm_WithNoRegistryServers_CarriesANullMcpSelection()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var vm = NewVmWithMcp(out var loginChecker, [profile]);
        loginChecker.IsLoggedIn(profile).Returns(true);
        await vm.LoadAsync();

        NewSessionResult? result = null;
        vm.CloseRequested += r => result = r;
        vm.ConfirmCommand.Execute(null);

        result.Should().NotBeNull();
        result!.EnabledMcpServerNames.Should().BeNull();
    }

    [Fact]
    public async Task SelectingAPluginProfileWithNoTtyProvider_ForcesSdkKind_SoStartNeverSilentlyLaunchesClaude()
    {
        var plugin = new SessionProfile("codex", new PluginProviderConfig("cli-agent-provider.codex", "{}"));
        var registry = Substitute.For<IPluginTtyProviderRegistry>();
        registry.Resolve("cli-agent-provider.codex").Returns((TtyProviderRegistration?)null);
        var resolver = Substitute.For<ITtySessionProviderResolver>();
        resolver.Resolve(plugin).Returns((ITtySessionProvider?)null);
        var vm = NewVmWithTty(out _, [plugin], resolver, registry);

        await vm.LoadAsync();

        vm.HasTtyProvider.Should().BeFalse();
        vm.SelectedKind.Should().Be(SessionKind.Sdk);
    }

    [Fact]
    public async Task SelectingAPluginProfileWithATtyProvider_KeepsTtyAvailable_AndDeclaredOptionsRender()
    {
        var plugin = new SessionProfile("codex", new PluginProviderConfig("cli-agent-provider.codex", "{}"));
        var registry = Substitute.For<IPluginTtyProviderRegistry>();
        registry.Resolve("cli-agent-provider.codex").Returns(new TtyProviderRegistration(
            "cli-agent-provider.codex",
            "Codex (CLI)",
            _ => Substitute.For<IPluginTtyProvider>(),
            Options: [new PluginTtyLaunchOption("sandbox", "Sandbox", ["read-only", "workspace-write"])]));
        var resolver = Substitute.For<ITtySessionProviderResolver>();
        resolver.Resolve(plugin).Returns(Substitute.For<ITtySessionProvider>());
        var vm = NewVmWithTty(out _, [plugin], resolver, registry);

        await vm.LoadAsync();
        vm.SelectTtyCommand.Execute(null);

        vm.HasTtyProvider.Should().BeTrue();
        vm.SelectedKind.Should().Be(SessionKind.Tty);
        vm.ShowPluginTtyOptions.Should().BeTrue();
        vm.PluginTtyOptions.Should().ContainSingle(option => option.Key == "sandbox" && option.Label == "Sandbox");
        vm.ShowSessionOptions.Should().BeFalse("mode/model/effort are Claude's own vocabulary, not this plugin's");
    }

    [Fact]
    public async Task Confirm_ForAPluginTtySession_CarriesTheChosenPluginOptionsButNotABlankOne()
    {
        var plugin = new SessionProfile("codex", new PluginProviderConfig("cli-agent-provider.codex", "{}"));
        var registry = Substitute.For<IPluginTtyProviderRegistry>();
        registry.Resolve("cli-agent-provider.codex").Returns(new TtyProviderRegistration(
            "cli-agent-provider.codex",
            "Codex (CLI)",
            _ => Substitute.For<IPluginTtyProvider>(),
            Options:
            [
                new PluginTtyLaunchOption("sandbox", "Sandbox", ["read-only", "workspace-write"]),
                new PluginTtyLaunchOption("model", "Model", []),
            ]));
        var resolver = Substitute.For<ITtySessionProviderResolver>();
        resolver.Resolve(plugin).Returns(Substitute.For<ITtySessionProvider>());
        var vm = NewVmWithTty(out _, [plugin], resolver, registry);
        await vm.LoadAsync();
        vm.SelectTtyCommand.Execute(null);
        vm.PluginTtyOptions.Single(option => option.Key == "sandbox").Value = "workspace-write";

        NewSessionResult? result = null;
        vm.CloseRequested += r => result = r;
        vm.ConfirmCommand.Execute(null);

        result.Should().NotBeNull();
        result!.PluginTtyOptions.Should().NotBeNull();
        result.PluginTtyOptions!.Should().ContainSingle();
        result.PluginTtyOptions["sandbox"].Should().Be("workspace-write");
    }

    [Fact]
    public async Task SelectingAnSdkPluginProfile_RendersItsDeclaredLaunchOptions()
    {
        var plugin = new SessionProfile("codex", new PluginProviderConfig("cli-agent-provider.codex", "{}"));
        var registry = Substitute.For<IPluginProviderRegistry>();
        registry.Resolve("cli-agent-provider.codex").Returns(_SessionRegistration(
            [new PluginSessionLaunchOption("sandbox", "Sandbox", ["read-only", "workspace-write"])]));
        var vm = NewVmWithSessionProvider([plugin], registry);

        await vm.LoadAsync();

        // A plugin profile with no TTY provider forces SDK kind, where its declared options render.
        vm.SelectedKind.Should().Be(SessionKind.Sdk);
        vm.ShowSdkLaunchOptions.Should().BeTrue();
        vm.SdkLaunchOptions.Should().ContainSingle(option => option.Key == "sandbox" && option.Label == "Sandbox");
    }

    [Fact]
    public async Task Confirm_ForAnSdkPluginSession_CarriesTheChosenSdkOptionsButNotABlankOne()
    {
        var plugin = new SessionProfile("codex", new PluginProviderConfig("cli-agent-provider.codex", "{}"));
        var registry = Substitute.For<IPluginProviderRegistry>();
        registry.Resolve("cli-agent-provider.codex").Returns(_SessionRegistration(
        [
            new PluginSessionLaunchOption("sandbox", "Sandbox", ["read-only", "workspace-write"]),
            new PluginSessionLaunchOption("model", "Model", []),
        ]));
        var vm = NewVmWithSessionProvider([plugin], registry);
        await vm.LoadAsync();
        vm.SdkLaunchOptions.Single(option => option.Key == "sandbox").Value = "workspace-write";

        NewSessionResult? result = null;
        vm.CloseRequested += r => result = r;
        vm.ConfirmCommand.Execute(null);

        result.Should().NotBeNull();
        result!.SdkLaunchOptions.Should().NotBeNull();
        result.SdkLaunchOptions!.Should().ContainSingle();
        result.SdkLaunchOptions["sandbox"].Should().Be("workspace-write");
    }

    [Fact]
    public async Task SelectingAnSdkPluginProfile_UpgradesTheModelOption_FromTheProvidersLiveResolver()
    {
        var plugin = new SessionProfile("codex", new PluginProviderConfig("cli-agent-provider.codex", "{}"));
        var registry = Substitute.For<IPluginProviderRegistry>();
        registry.Resolve("cli-agent-provider.codex").Returns(_SessionRegistration(
            [
                new PluginSessionLaunchOption("sandbox", "Sandbox", ["read-only", "workspace-write"], "read-only"),
                new PluginSessionLaunchOption("model", "Model", []),
            ],
            resolveOptionsAsync: (_, _) => Task.FromResult<IReadOnlyList<PluginSessionLaunchOption>>(
            [
                new PluginSessionLaunchOption("sandbox", "Sandbox", ["read-only", "workspace-write"], "read-only"),
                new PluginSessionLaunchOption("model", "Model", ["gpt-5.6-terra", "gpt-5.6-luna"], "gpt-5.6-terra"),
            ])));
        var vm = NewVmWithSessionProvider([plugin], registry);

        await vm.LoadAsync();
        await vm.LaunchOptionsRefresh;

        // The free-text Model becomes a dropdown of the provider's live models, defaulted to its chosen default.
        var model = vm.SdkLaunchOptions.Single(option => option.Key == "model");
        model.Choices.Should().Equal("gpt-5.6-terra", "gpt-5.6-luna");
        model.Value.Should().Be("gpt-5.6-terra");
    }

    [Fact]
    public async Task SelectingAnSdkPluginProfile_KeepsTheDeclaredFreeTextModel_WhenTheLiveResolverFails()
    {
        var plugin = new SessionProfile("codex", new PluginProviderConfig("cli-agent-provider.codex", "{}"));
        var registry = Substitute.For<IPluginProviderRegistry>();
        registry.Resolve("cli-agent-provider.codex").Returns(_SessionRegistration(
            [new PluginSessionLaunchOption("model", "Model", [])],
            resolveOptionsAsync: (_, _) => Task.FromException<IReadOnlyList<PluginSessionLaunchOption>>(
                new InvalidOperationException("codex is not logged in"))));
        var vm = NewVmWithSessionProvider([plugin], registry);

        await vm.LoadAsync();
        await vm.LaunchOptionsRefresh;

        // A failing model/list must never blow away the declared option — Model stays a free-text field.
        var model = vm.SdkLaunchOptions.Single(option => option.Key == "model");
        model.Choices.Should().BeEmpty();
        model.IsFreeText.Should().BeTrue();
    }

    [Fact]
    public async Task SdkOptionRefresh_ForASupersededProfile_DoesNotClobberTheNewlySelectedProfilesOptions()
    {
        var profileA = new SessionProfile("codex-a", new PluginProviderConfig("cli-agent-provider.codex", """{"tag":"A"}"""));
        var profileB = new SessionProfile("codex-b", new PluginProviderConfig("cli-agent-provider.codex", """{"tag":"B"}"""));

        // Profile A's resolve is gated open (still running); B's returns immediately. Both resolve through the one
        // registration, told apart by the config JSON the dialog passes.
        var gateA = new TaskCompletionSource<IReadOnlyList<PluginSessionLaunchOption>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = Substitute.For<IPluginProviderRegistry>();
        registry.Resolve("cli-agent-provider.codex").Returns(_SessionRegistration(
            [new PluginSessionLaunchOption("model", "Model", [])],
            resolveOptionsAsync: (configJson, _) => configJson.Contains("\"A\"")
                ? gateA.Task
                : Task.FromResult<IReadOnlyList<PluginSessionLaunchOption>>([new PluginSessionLaunchOption("model", "Model", ["b-model"], "b-model")])));
        var vm = NewVmWithSessionProvider([profileA, profileB], registry);

        await vm.LoadAsync();
        vm.SelectedProfile = profileA;
        var refreshA = vm.LaunchOptionsRefresh;
        vm.SelectedProfile = profileB;
        await vm.LaunchOptionsRefresh;

        vm.SdkLaunchOptions.Single(option => option.Key == "model").Choices.Should().Equal("b-model");

        // A's resolve now completes, late. The stale-guard must drop it rather than overwrite B's options.
        gateA.SetResult([new PluginSessionLaunchOption("model", "Model", ["a-model"], "a-model")]);
        await refreshA;

        vm.SdkLaunchOptions.Single(option => option.Key == "model").Choices.Should().Equal("b-model");
    }

    [Fact]
    public async Task SelectingATtyPluginProfile_UpgradesTheModelOption_FromTheProvidersLiveResolver()
    {
        var plugin = new SessionProfile("codex", new PluginProviderConfig("cli-agent-provider.codex", "{}"));
        var registry = Substitute.For<IPluginTtyProviderRegistry>();
        registry.Resolve("cli-agent-provider.codex").Returns(new TtyProviderRegistration(
            "cli-agent-provider.codex",
            "Codex (CLI)",
            _ => Substitute.For<IPluginTtyProvider>(),
            Options:
            [
                new PluginTtyLaunchOption("sandbox", "Sandbox", ["read-only", "workspace-write"]),
                new PluginTtyLaunchOption("model", "Model", []),
            ])
        {
            ResolveOptionsAsync = (_, _) => Task.FromResult<IReadOnlyList<PluginTtyLaunchOption>>(
            [
                new PluginTtyLaunchOption("sandbox", "Sandbox", ["read-only", "workspace-write"]),
                new PluginTtyLaunchOption("model", "Model", ["gpt-5.6-terra", "gpt-5.6-luna"], "gpt-5.6-terra"),
            ]),
        });
        var resolver = Substitute.For<ITtySessionProviderResolver>();
        resolver.Resolve(plugin).Returns(Substitute.For<ITtySessionProvider>());
        var vm = NewVmWithTty(out _, [plugin], resolver, registry);

        await vm.LoadAsync();
        vm.SelectTtyCommand.Execute(null);
        await vm.LaunchOptionsRefresh;

        // The TTY route gets the same live model/list upgrade as the SDK route.
        var model = vm.PluginTtyOptions.Single(option => option.Key == "model");
        model.Choices.Should().Equal("gpt-5.6-terra", "gpt-5.6-luna");
        model.Value.Should().Be("gpt-5.6-terra");
    }

    [Fact]
    public async Task SelectingANoTtyProfileThatForcesSdk_FromATtyState_RunsTheLiveResolverExactlyOnce()
    {
        var ttyProfile = new SessionProfile("codex-tty", new PluginProviderConfig("tty-only", "{}"));
        var sdkOnlyProfile = new SessionProfile("codex-sdk", new PluginProviderConfig("sdk-only", "{}"));

        // The first profile has a TTY provider (so the operator can be on the Tty kind); the second has none, so
        // selecting it forces the kind back to Sdk — the path where the kind change used to fire a second refresh.
        var ttyResolver = Substitute.For<ITtySessionProviderResolver>();
        ttyResolver.Resolve(ttyProfile).Returns(Substitute.For<ITtySessionProvider>());
        ttyResolver.Resolve(sdkOnlyProfile).Returns((ITtySessionProvider?)null);

        var invocations = new int[1];
        var sessionRegistry = Substitute.For<IPluginProviderRegistry>();
        sessionRegistry.Resolve("sdk-only").Returns(_SessionRegistration(
            [new PluginSessionLaunchOption("model", "Model", [])],
            resolveOptionsAsync: (_, _) =>
            {
                Interlocked.Increment(ref invocations[0]);
                return Task.FromResult<IReadOnlyList<PluginSessionLaunchOption>>([new PluginSessionLaunchOption("model", "Model", ["m"], "m")]);
            }));

        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<SessionProfile> { ttyProfile, sdkOnlyProfile });
        var loginChecker = Substitute.For<IClaudeProfileLoginChecker>();
        loginChecker.IsLoggedIn(Arg.Any<SessionProfile>()).Returns(true);
        var vm = new NewSessionDialogViewModel(
            store, loginChecker, mcpServerStore: null, workingPathStore: null, conversationPickers: null,
            ttyResolver, ttyProviderRegistry: null, sessionRegistry);

        await vm.LoadAsync();          // selects the TTY profile
        vm.SelectTtyCommand.Execute(null);
        vm.SelectedProfile = sdkOnlyProfile;   // forces Tty -> Sdk, which must not double-fire the refresh
        await vm.LaunchOptionsRefresh;

        invocations[0].Should().Be(1);
        vm.SdkLaunchOptions.Single(option => option.Key == "model").Choices.Should().Equal("m");
    }

    private static SessionProviderRegistration _SessionRegistration(
        IReadOnlyList<PluginSessionLaunchOption> options,
        Func<string, CancellationToken, Task<IReadOnlyList<PluginSessionLaunchOption>>>? resolveOptionsAsync = null) =>
        new(
            "cli-agent-provider.codex",
            "Codex (CLI)",
            _ => Substitute.For<IPluginSessionDriverFactory>(),
            new PluginSessionCapabilities(SupportsTools: true, SupportsPermissions: true),
            _ => Substitute.For<IPluginProviderConfigView>())
        {
            Options = options,
            ResolveOptionsAsync = resolveOptionsAsync,
        };

    private static NewSessionDialogViewModel NewVmWithSessionProvider(SessionProfile[] profiles, IPluginProviderRegistry sessionProviderRegistry)
    {
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(profiles.ToList());
        var loginChecker = Substitute.For<IClaudeProfileLoginChecker>();
        foreach (var profile in profiles)
        {
            loginChecker.IsLoggedIn(profile).Returns(true);
        }

        return new NewSessionDialogViewModel(
            store, loginChecker, mcpServerStore: null, workingPathStore: null, conversationPickers: null,
            ttyProviderResolver: null, ttyProviderRegistry: null, sessionProviderRegistry);
    }

    private static NewSessionDialogViewModel NewVm(out IClaudeProfileLoginChecker loginChecker, params SessionProfile[] profiles)
    {
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(profiles.ToList());
        loginChecker = Substitute.For<IClaudeProfileLoginChecker>();
        foreach (var profile in profiles)
        {
            loginChecker.IsLoggedIn(profile).Returns(true);
        }

        return new NewSessionDialogViewModel(store, loginChecker);
    }

    private static NewSessionDialogViewModel NewVmWithTty(
        out IClaudeProfileLoginChecker loginChecker,
        SessionProfile[] profiles,
        ITtySessionProviderResolver ttyProviderResolver,
        IPluginTtyProviderRegistry ttyProviderRegistry)
    {
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(profiles.ToList());
        loginChecker = Substitute.For<IClaudeProfileLoginChecker>();
        foreach (var profile in profiles)
        {
            loginChecker.IsLoggedIn(profile).Returns(true);
        }

        return new NewSessionDialogViewModel(
            store, loginChecker, mcpServerStore: null, workingPathStore: null, conversationPickers: null,
            ttyProviderResolver, ttyProviderRegistry);
    }

    private static NewSessionDialogViewModel NewVmWithMcp(
        out IClaudeProfileLoginChecker loginChecker,
        SessionProfile[] profiles,
        params McpServerConfig[] registry)
    {
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(profiles.ToList());
        loginChecker = Substitute.For<IClaudeProfileLoginChecker>();
        foreach (var profile in profiles)
        {
            loginChecker.IsLoggedIn(profile).Returns(true);
        }

        var mcpServerStore = Substitute.For<IMcpServerStore>();
        mcpServerStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(registry.ToList());

        return new NewSessionDialogViewModel(store, loginChecker, mcpServerStore);
    }
}
