using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.WorkingPaths;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.WorkingPaths;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Sessions;
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

        Assert.Equal(new[] { work, personal }, vm.Profiles);
        Assert.Equal(work, vm.SelectedProfile);
    }

    [Fact]
    public async Task SelectingProfile_PreFillsTheGenericOptions_FromItsSavedOptionDefaults()
    {
        var registry = Substitute.For<IPluginProviderRegistry>();
        registry.Resolve("claude").Returns(new SessionProviderRegistration(
            "claude", "Claude",
            _ => Substitute.For<IPluginSessionDriverFactory>(),
            new PluginSessionCapabilities(SupportsTools: true, SupportsPermissions: true),
            _ => Substitute.For<IPluginProviderConfigView>())
        {
            Options =
            [
                new PluginSessionLaunchOption("permission-mode", "Permission mode", ["default", "bypassPermissions"], "default"),
                new PluginSessionLaunchOption("model", "Model", ["opus", "sonnet"]),
                new PluginSessionLaunchOption("effort", "Effort", ["low", "medium", "high"], "medium"),
            ],
        });
        var profile = new SessionProfile(
            "work",
            new PluginProviderConfig("claude", "{}"),
            Defaults: new ProfileDefaults(string.Empty, string.Empty, string.Empty)
            {
                OptionDefaults = new Dictionary<string, string>
                {
                    ["permission-mode"] = "bypassPermissions",
                    ["model"] = "opus",
                    ["effort"] = "high",
                },
            });
        var vm = NewVmWithSessionProvider([profile], registry);

        await vm.LoadAsync();

        // Fase 4: the dialog pre-selects the provider's own options from the profile's saved OptionDefaults, not the
        // retired typed permission/model/effort fields (which are decoupled from the dialog now).
        Assert.Equal("bypassPermissions", vm.SdkLaunchOptions.Single(option => option.Key == "permission-mode").Value);
        Assert.Equal("opus", vm.SdkLaunchOptions.Single(option => option.Key == "model").Value);
        Assert.Equal("high", vm.SdkLaunchOptions.Single(option => option.Key == "effort").Value);
    }

    [Fact]
    public async Task SelectingProfileWithoutDefaults_FallsBackToTheAppDefaults()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var vm = NewVm(out _, profile);
        await vm.LoadAsync();

        Assert.Equal(SessionOptionCatalog.DefaultPermissionMode, vm.SelectedPermissionMode);
        Assert.Equal(SessionOptionCatalog.DefaultModel.Value, vm.SelectedClaudeModel);
        Assert.Equal(SessionOptionCatalog.DefaultEffort, vm.SelectedEffort);
    }

    [Fact]
    public async Task CanStart_IsFalseForSdkWhenTheSelectedProfileIsNotLoggedIn()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var vm = NewVm(out var loginChecker, profile);
        loginChecker.IsLoggedIn(profile).Returns(false);

        await vm.LoadAsync();
        vm.SelectSdkCommand.Execute(null);

        Assert.False(vm.IsSelectedProfileLoggedIn);
        Assert.False(vm.CanStart);
        Assert.False(vm.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public async Task ForAMigratedClaudePluginProfile_TheLoginGateAndResume_StillApply()
    {
        // Regression (adversarial review): after migration a Claude profile is a PluginProviderConfig, not ClaudeCli, so
        // the login gate and resume UI — which keyed off the old provider value — were silently disabled: a Claude SDK
        // session became startable while logged out and the resume controls vanished. Now they key off Claude config
        // presence, which a migrated profile still has.
        var profile = new SessionProfile("work", ClaudePluginProfile.Create("/home/r/.claude-work", null));
        var vm = NewVm(out var loginChecker, profile);
        loginChecker.IsLoggedIn(profile).Returns(false);

        await vm.LoadAsync();
        vm.SelectSdkCommand.Execute(null);

        Assert.True(vm.IsClaudeProfile);
        Assert.False(vm.IsLocalProfile);
        Assert.True(vm.ShowResumeOptions);
        Assert.False(vm.IsSelectedProfileLoggedIn);
        Assert.False(vm.CanStart);
    }

    [Fact]
    public async Task CanStart_IsTrueForTtyEvenWhenNotLoggedIn_SinceTheTuiRunsItsOwnLogin()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var vm = NewVm(out var loginChecker, profile);
        loginChecker.IsLoggedIn(profile).Returns(false);

        await vm.LoadAsync(); // the default kind is TTY

        Assert.True(vm.IsTty);
        Assert.False(vm.IsSelectedProfileLoggedIn);
        Assert.True(vm.CanStart);
        Assert.True(vm.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public async Task ShowLoginHint_IsShownForSdkButNotForTty_WhichLogsInItself()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var vm = NewVm(out var loginChecker, profile);
        loginChecker.IsLoggedIn(profile).Returns(false);
        await vm.LoadAsync();

        vm.SelectSdkCommand.Execute(null);
        Assert.True(vm.ShowLoginHint);

        vm.SelectTtyCommand.Execute(null);
        Assert.False(vm.ShowLoginHint);
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

        Assert.True(vm.IsLocalProfile);
        Assert.True(vm.CanStart);
        Assert.False(vm.ShowSessionOptions);
        Assert.Equal("Ollama", vm.SelectedProviderLabel);
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

        Assert.Equal(SessionKind.Sdk, vm.SelectedKind);
    }

    [Fact]
    public async Task Confirm_RaisesCloseWithTheChosenProfileAndOptions()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var vm = NewVm(out var loginChecker, profile);
        loginChecker.IsLoggedIn(profile).Returns(true);
        await vm.LoadAsync();
        vm.SelectedClaudeModel = "haiku";

        NewSessionResult? result = null;
        var closed = false;
        vm.CloseRequested += r => { result = r; closed = true; };

        vm.ConfirmCommand.Execute(null);

        Assert.True(closed);
        Assert.NotNull(result);
        Assert.Equal(profile, result!.Profile);
        Assert.Equal("haiku", result.Model.Value);
    }

    [Fact]
    public async Task Confirm_CarriesATypedCustomModel_NotJustTheKnownAliases()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var vm = NewVm(out var loginChecker, profile);
        loginChecker.IsLoggedIn(profile).Returns(true);
        await vm.LoadAsync();
        // The editable field lets the operator pin a specific model/snapshot, not only the alias suggestions.
        vm.SelectedClaudeModel = "claude-opus";

        NewSessionResult? result = null;
        vm.CloseRequested += r => result = r;
        vm.ConfirmCommand.Execute(null);

        Assert.Equal("claude-opus", result!.Model.Value);
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

        Assert.True(closed);
        Assert.Null(result);
    }

    [Fact]
    public void DefaultKind_IsTty_TheOperatorsPreferredDefault()
    {
        var vm = NewVm(out _);

        Assert.Equal(SessionKind.Tty, vm.SelectedKind);
        Assert.True(vm.IsTty);
        Assert.False(vm.IsSdk);
    }

    [Fact]
    public void SelectTty_SwitchesIsSdkAndIsTty()
    {
        var vm = NewVm(out _);

        vm.SelectTtyCommand.Execute(null);

        Assert.Equal(SessionKind.Tty, vm.SelectedKind);
        Assert.False(vm.IsSdk);
        Assert.True(vm.IsTty);

        vm.SelectSdkCommand.Execute(null);

        Assert.Equal(SessionKind.Sdk, vm.SelectedKind);
        Assert.True(vm.IsSdk);
        Assert.False(vm.IsTty);
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

        Assert.NotNull(result);
        Assert.Equal(SessionKind.Tty, result!.Kind);
    }

    [Fact]
    public void PermissionModes_IncludeBypass_SinceTheDialogIsWhereItCanBeChosen()
    {
        var vm = NewVm(out _);

        Assert.Contains("bypassPermissions", vm.PermissionModes.Select(mode => mode.Value));
    }

    [Fact]
    public async Task LoadAsync_PopulatesTheMcpChecklist_AllCheckedByDefault()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var vm = NewVmWithMcp(out _, [profile],
            new McpServerConfig { Name = "server-a" },
            new McpServerConfig { Name = "server-b" });

        await vm.LoadAsync();

        Assert.True(vm.HasMcpServers);
        Assert.Equal(new[] { "server-a", "server-b" }, vm.McpServers.Select(server => server.Name));
        Assert.All(vm.McpServers, server => Assert.True(server.IsEnabledForSession));
    }

    [Fact]
    public async Task LoadAsync_ExcludesDisabledRegistryServers_FromTheChecklist()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var vm = NewVmWithMcp(out _, [profile],
            new McpServerConfig { Name = "on" },
            new McpServerConfig { Name = "off", Enabled = false });

        await vm.LoadAsync();

        Assert.Equal(new[] { "on" }, vm.McpServers.Select(server => server.Name));
    }

    [Fact]
    public async Task LoadAsync_ExcludesInternalEndpoints_FromTheChecklist()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var vm = NewVmWithMcp(out _, [profile],
            new McpServerConfig { Name = "server-a" },
            // An internal-only endpoint (AC-204, the Autopilot CEO/step tools) is enabled and mountable but must not
            // appear in the operator's checklist. Red without the fix, which offered every enabled catalog server.
            new McpServerConfig { Name = "cockpit-autopilot-ceo", Url = "http://127.0.0.1:1/mcp", Internal = true });

        await vm.LoadAsync();

        Assert.Equal(new[] { "server-a" }, vm.McpServers.Select(server => server.Name));
    }

    [Fact]
    public async Task LoadAsync_WithNoRegistryServers_HasMcpServersIsFalse()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var vm = NewVmWithMcp(out _, [profile]);

        await vm.LoadAsync();

        Assert.False(vm.HasMcpServers);
        Assert.Empty(vm.McpServers);
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

        Assert.NotNull(result);
        Assert.Equivalent(new object[] { "server-a" }, result!.EnabledMcpServerNames);
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

        Assert.NotNull(result);
        Assert.Null(result!.EnabledMcpServerNames);
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

        Assert.False(vm.HasTtyProvider);
        Assert.Equal(SessionKind.Sdk, vm.SelectedKind);
    }

    // AC-139: a profile can set its own default Kind, so the New-session dialog pre-selects the route it is
    // actually meant for (a TUI-averse Codex-style profile that should always open as SDK, say) instead of the
    // hard TTY default.
    [Fact]
    public async Task SelectingAProfileWithAnSdkDefaultKind_PreSelectsSdk()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work")) { DefaultKind = ProfileSessionKind.Sdk };
        var vm = NewVm(out _, profile);

        await vm.LoadAsync();

        Assert.Equal(SessionKind.Sdk, vm.SelectedKind);
    }

    [Fact]
    public async Task SelectingAProfileWithATtyDefaultKind_PreSelectsTty()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work")) { DefaultKind = ProfileSessionKind.Tty };
        var vm = NewVm(out _, profile);

        await vm.LoadAsync();

        Assert.Equal(SessionKind.Tty, vm.SelectedKind);
    }

    [Fact]
    public async Task PerSession_TheKindToggleStillOverrulesTheProfilesDefaultKind()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work")) { DefaultKind = ProfileSessionKind.Sdk };
        var vm = NewVm(out _, profile);
        await vm.LoadAsync();
        Assert.Equal(SessionKind.Sdk, vm.SelectedKind);

        vm.SelectTtyCommand.Execute(null);

        Assert.Equal(SessionKind.Tty, vm.SelectedKind);
    }

    // AC-139 pitfall 1: a profile that says "TTY" but has no TTY route to run at all must not land on TTY — the
    // force-SDK branch keeps winning over the profile's saved default.
    [Fact]
    public async Task AProfileWithNoTtyProvider_ForcesSdk_EvenWhenItsSavedDefaultKindIsTty()
    {
        var plugin = new SessionProfile("codex", new PluginProviderConfig("cli-agent-provider.codex", "{}")) { DefaultKind = ProfileSessionKind.Tty };
        var registry = Substitute.For<IPluginTtyProviderRegistry>();
        registry.Resolve("cli-agent-provider.codex").Returns((TtyProviderRegistration?)null);
        var resolver = Substitute.For<ITtySessionProviderResolver>();
        resolver.Resolve(plugin).Returns((ITtySessionProvider?)null);
        var vm = NewVmWithTty(out _, [plugin], resolver, registry);

        await vm.LoadAsync();

        Assert.False(vm.HasTtyProvider);
        Assert.Equal(SessionKind.Sdk, vm.SelectedKind);
    }

    // AC-139/AC-6: an existing profile saved before this setting existed carries no DefaultKind at all — it must
    // keep behaving exactly like today, defaulting to TTY.
    [Fact]
    public async Task AProfileWithNoSavedDefaultKind_StillPreSelectsTty_SoAnOlderProfileBehavesExactlyAsBefore()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        Assert.Null(profile.DefaultKind);
        var vm = NewVm(out _, profile);

        await vm.LoadAsync();

        Assert.Equal(SessionKind.Tty, vm.SelectedKind);
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

        Assert.True(vm.HasTtyProvider);
        Assert.Equal(SessionKind.Tty, vm.SelectedKind);
        Assert.True(vm.ShowPluginTtyOptions);
        Assert.Single(vm.PluginTtyOptions, option => option.Key == "sandbox" && option.Label == "Sandbox");
        Assert.False(vm.ShowSessionOptions, "mode/model/effort are Claude's own vocabulary, not this plugin's");
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

        Assert.NotNull(result);
        Assert.NotNull(result!.PluginTtyOptions);
        Assert.Single(result.PluginTtyOptions!);
        Assert.Equal("workspace-write", result.PluginTtyOptions["sandbox"]);
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
        Assert.Equal(SessionKind.Sdk, vm.SelectedKind);
        Assert.True(vm.ShowSdkLaunchOptions);
        Assert.Single(vm.SdkLaunchOptions, option => option.Key == "sandbox" && option.Label == "Sandbox");
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

        Assert.NotNull(result);
        Assert.NotNull(result!.SdkLaunchOptions);
        Assert.Single(result.SdkLaunchOptions!);
        Assert.Equal("workspace-write", result.SdkLaunchOptions["sandbox"]);
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
        Assert.Equal(new[] { "gpt-5.6-terra", "gpt-5.6-luna" }, model.Choices);
        Assert.Equal("gpt-5.6-terra", model.Value);
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
        Assert.Empty(model.Choices);
        Assert.True(model.IsFreeText);
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

        Assert.Equal(new[] { "b-model" }, vm.SdkLaunchOptions.Single(option => option.Key == "model").Choices);

        // A's resolve now completes, late. The stale-guard must drop it rather than overwrite B's options.
        gateA.SetResult([new PluginSessionLaunchOption("model", "Model", ["a-model"], "a-model")]);
        await refreshA;

        Assert.Equal(new[] { "b-model" }, vm.SdkLaunchOptions.Single(option => option.Key == "model").Choices);
    }

    [Fact]
    public void PluginOptionRow_ShowsProviderLabels_AndFallsBackToTheValue_WhenUnlabelled()
    {
        var row = new PluginTtyOptionSelectionViewModel(
            "model", "Model", ["opus", "sonnet", "custom-snapshot"], "sonnet",
            new Dictionary<string, string> { ["opus"] = "Opus", ["sonnet"] = "Sonnet" });

        // Fase 4 step 1: a value with a provider label reads friendly; an unlabelled value (a pinned snapshot) falls
        // back to showing itself, and the picked value is always the raw CLI value regardless of its label.
        Assert.Equal(new[] { "Opus", "Sonnet", "custom-snapshot" }, row.ChoiceItems.Select(choice => choice.Label));
        Assert.Equal("Sonnet", row.ChoiceItems.Single(choice => choice.Value == "sonnet").Label);
        Assert.Equal("sonnet", row.Value);
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
        Assert.Equal(new[] { "gpt-5.6-terra", "gpt-5.6-luna" }, model.Choices);
        Assert.Equal("gpt-5.6-terra", model.Value);
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
        var loginChecker = Substitute.For<IProfileLoginChecker>();
        loginChecker.IsLoggedIn(Arg.Any<SessionProfile>()).Returns(true);
        var vm = new NewSessionDialogViewModel(
            store, loginChecker, mcpServerCatalog: null, workingPathStore: null, conversationPickers: null,
            ttyResolver, ttyProviderRegistry: null, sessionRegistry);

        await vm.LoadAsync();          // selects the TTY profile
        vm.SelectTtyCommand.Execute(null);
        vm.SelectedProfile = sdkOnlyProfile;   // forces Tty -> Sdk, which must not double-fire the refresh
        await vm.LaunchOptionsRefresh;

        Assert.Equal(1, invocations[0]);
        Assert.Equal(new[] { "m" }, vm.SdkLaunchOptions.Single(option => option.Key == "model").Choices);
    }

    // AC-139/AC-5: seeding Kind from a profile's saved SDK default (on a profile that does have a TTY route, so
    // the seed is the profile default winning, not the force-SDK branch) must still fire only the profile switch's
    // own single refresh, not a second one from the kind change it causes.
    [Fact]
    public async Task OpeningTheDialogOnAProfileWithAnSdkDefaultKind_RunsTheLiveResolverExactlyOnce()
    {
        var profile = new SessionProfile("codex", new PluginProviderConfig("codex", "{}")) { DefaultKind = ProfileSessionKind.Sdk };

        var ttyResolver = Substitute.For<ITtySessionProviderResolver>();
        ttyResolver.Resolve(profile).Returns(Substitute.For<ITtySessionProvider>());

        var invocations = new int[1];
        var sessionRegistry = Substitute.For<IPluginProviderRegistry>();
        sessionRegistry.Resolve("codex").Returns(_SessionRegistration(
            [new PluginSessionLaunchOption("model", "Model", [])],
            resolveOptionsAsync: (_, _) =>
            {
                Interlocked.Increment(ref invocations[0]);
                return Task.FromResult<IReadOnlyList<PluginSessionLaunchOption>>([new PluginSessionLaunchOption("model", "Model", ["m"], "m")]);
            }));

        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<SessionProfile> { profile });
        var loginChecker = Substitute.For<IProfileLoginChecker>();
        loginChecker.IsLoggedIn(Arg.Any<SessionProfile>()).Returns(true);
        var vm = new NewSessionDialogViewModel(
            store, loginChecker, mcpServerCatalog: null, workingPathStore: null, conversationPickers: null,
            ttyResolver, ttyProviderRegistry: null, sessionRegistry);

        await vm.LoadAsync();
        await vm.LaunchOptionsRefresh;

        Assert.Equal(SessionKind.Sdk, vm.SelectedKind);
        Assert.Equal(1, invocations[0]);
    }

    [Fact]
    public async Task PickingAConversation_SetsBothTheSessionIdAndTheFolderItRanIn()
    {
        var pickers = new ConversationPickerRegistry();
        pickers.Register(new ConversationPickerRegistration("Search transcripts", () => Task.FromResult<string?>("sess-42"))
        {
            PickWithLocationAsync = () => Task.FromResult<PickedConversation?>(new PickedConversation("sess-42", "/home/me/RiderProjects/App")),
        });

        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<SessionProfile> { new("personal", new ClaudeConfig("/home/r/.claude-personal")) });
        var loginChecker = Substitute.For<IProfileLoginChecker>();
        loginChecker.IsLoggedIn(Arg.Any<SessionProfile>()).Returns(true);
        var vm = new NewSessionDialogViewModel(
            store, loginChecker, mcpServerCatalog: null, workingPathStore: null, conversationPickers: pickers,
            ttyProviderResolver: null, ttyProviderRegistry: null);

        await vm.PickConversationCommand.ExecuteAsync(null);

        Assert.Equal("sess-42", vm.ResumeSessionId);
        Assert.Equal(SessionResumeMode.BySessionId, vm.ResumeMode);
        Assert.Equal("/home/me/RiderProjects/App", vm.WorkingDirectory);
    }

    [Fact]
    public async Task PickingFromAnIdOnlyPicker_ResumesTheSessionButLeavesTheWorkingDirectoryUntouched()
    {
        var pickers = new ConversationPickerRegistry();
        pickers.Register(new ConversationPickerRegistration("Search transcripts", () => Task.FromResult<string?>("sess-42")));

        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<SessionProfile> { new("personal", new ClaudeConfig("/home/r/.claude-personal")) });
        var loginChecker = Substitute.For<IProfileLoginChecker>();
        loginChecker.IsLoggedIn(Arg.Any<SessionProfile>()).Returns(true);
        var vm = new NewSessionDialogViewModel(
            store, loginChecker, mcpServerCatalog: null, workingPathStore: null, conversationPickers: pickers,
            ttyProviderResolver: null, ttyProviderRegistry: null)
        {
            WorkingDirectory = "/somewhere/else",
        };

        await vm.PickConversationCommand.ExecuteAsync(null);

        Assert.Equal("sess-42", vm.ResumeSessionId);
        Assert.Equal("/somewhere/else", vm.WorkingDirectory);
    }

    // --- AC-130: per-profile default working directory + MCP pre-selection ---

    [Fact]
    public async Task SelectingProfile_WithADefaultWorkingDirectory_PreFillsTheFolder()
    {
        var profile = new SessionProfile("app", new ClaudeConfig("/home/r/.claude"))
        {
            DefaultWorkingDirectory = "/home/r/RiderProjects/App",
        };
        var vm = NewVm(out _, profile);

        await vm.LoadAsync();

        Assert.Equal("/home/r/RiderProjects/App", vm.WorkingDirectory);
    }

    [Fact]
    public async Task SwitchingToAProfileWithoutADefaultFolder_ClearsThePreviousProfilesFolder()
    {
        var withFolder = new SessionProfile("app", new ClaudeConfig("/home/r/.claude")) { DefaultWorkingDirectory = "/home/r/App" };
        var withoutFolder = new SessionProfile("plain", new ClaudeConfig("/home/r/.claude"));
        var vm = NewVm(out _, withFolder, withoutFolder);
        await vm.LoadAsync();
        Assert.Equal("/home/r/App", vm.WorkingDirectory);

        vm.SelectedProfile = withoutFolder;

        Assert.Empty(vm.WorkingDirectory);
    }

    [Fact]
    public async Task SwitchingProfiles_BeforeTheOperatorTouchesTheFolder_AppliesEachProfilesDefault()
    {
        var a = new SessionProfile("a", new ClaudeConfig("/home/r/.claude")) { DefaultWorkingDirectory = "/home/r/A" };
        var b = new SessionProfile("b", new ClaudeConfig("/home/r/.claude")) { DefaultWorkingDirectory = "/home/r/B" };
        var vm = NewVm(out _, a, b);
        await vm.LoadAsync();
        Assert.Equal("/home/r/A", vm.WorkingDirectory);

        vm.SelectedProfile = b;

        Assert.Equal("/home/r/B", vm.WorkingDirectory);
    }

    [Fact]
    public async Task SwitchingProfiles_AfterTheOperatorSetAFolder_KeepsTheirFolder()
    {
        // Review finding 1: a profile switch must not silently discard a folder the operator chose. Once touched, the
        // folder is sticky — the same guarantee that protects it across the Manage-profiles reload.
        var a = new SessionProfile("a", new ClaudeConfig("/home/r/.claude")) { DefaultWorkingDirectory = "/home/r/A" };
        var b = new SessionProfile("b", new ClaudeConfig("/home/r/.claude")) { DefaultWorkingDirectory = "/home/r/B" };
        var vm = NewVm(out _, a, b);
        await vm.LoadAsync();
        vm.WorkingDirectory = "/home/r/chosen-by-hand";

        vm.SelectedProfile = b;

        Assert.Equal("/home/r/chosen-by-hand", vm.WorkingDirectory);
    }

    [Fact]
    public async Task ReloadingAfterManagingProfiles_KeepsAFolderTheOperatorChose()
    {
        // The exact confirmed scenario: type a folder, open Manage profiles, return — which re-runs LoadAsync. The
        // operator's folder must survive that reload rather than reset to the (default-less) first profile's blank.
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude"));
        var vm = NewVm(out _, profile);
        await vm.LoadAsync();
        vm.WorkingDirectory = "/home/r/chosen-by-hand";

        await vm.LoadAsync(); // the Manage-profiles round-trip reload

        Assert.Equal("/home/r/chosen-by-hand", vm.WorkingDirectory);
    }

    [Fact]
    public async Task SwitchingProfiles_AfterTheOperatorUntickedAnMcpServer_KeepsTheirTicks()
    {
        // Review finding 2: once the operator edits the checklist, flipping profiles must not re-apply a profile's
        // pre-selection over their deliberate unticks.
        var first = new SessionProfile("first", new ClaudeConfig("/home/r/.claude"));
        var second = new SessionProfile("second", new ClaudeConfig("/home/r/.claude"));
        var vm = NewVmWithMcp(out _, [first, second],
            new McpServerConfig { Name = "server-a" },
            new McpServerConfig { Name = "server-b" });
        await vm.LoadAsync();

        vm.McpServers.Single(server => server.Name == "server-b").IsEnabledForSession = false;
        vm.SelectedProfile = second;
        vm.SelectedProfile = first;

        Assert.False(vm.McpServers.Single(server => server.Name == "server-b").IsEnabledForSession);
    }

    [Fact]
    public async Task LoadAsync_WithAProfileMcpPreSelection_TicksOnlyTheSelectedServers()
    {
        var profile = new SessionProfile("app", new ClaudeConfig("/home/r/.claude"))
        {
            EnabledMcpServerNames = ["server-b"],
        };
        var vm = NewVmWithMcp(out _, [profile],
            new McpServerConfig { Name = "server-a" },
            new McpServerConfig { Name = "server-b" });

        await vm.LoadAsync();

        Assert.False(vm.McpServers.Single(server => server.Name == "server-a").IsEnabledForSession);
        Assert.True(vm.McpServers.Single(server => server.Name == "server-b").IsEnabledForSession);
    }

    [Fact]
    public async Task SwitchingProfiles_ReAppliesEachProfilesMcpPreSelection()
    {
        var restricted = new SessionProfile("restricted", new ClaudeConfig("/home/r/.claude")) { EnabledMcpServerNames = ["server-a"] };
        var unrestricted = new SessionProfile("open", new ClaudeConfig("/home/r/.claude"));
        var vm = NewVmWithMcp(out _, [restricted, unrestricted],
            new McpServerConfig { Name = "server-a" },
            new McpServerConfig { Name = "server-b" });
        await vm.LoadAsync();

        // The restricted profile is first: only its named server is ticked.
        Assert.False(vm.McpServers.Single(server => server.Name == "server-b").IsEnabledForSession);

        // Switching to the unrestricted profile re-ticks everything (null = no restriction).
        vm.SelectedProfile = unrestricted;
        Assert.All(vm.McpServers, server => Assert.True(server.IsEnabledForSession));
    }

    // --- AC-131: managing the remembered-folders quick-pick ---

    [Fact]
    public async Task RememberedPaths_WithBothFavoritesAndRecents_HasASeparatorBetweenThem()
    {
        var vm = NewVmWithWorkingPaths(new WorkingPathHistory([@"C:\recent"], [@"C:\fav"]), out _);

        await vm.LoadAsync();

        var favIndex = _IndexOf(vm, o => o.IsFavorite);
        var sepIndex = _IndexOf(vm, o => o.IsSeparator);
        var recentIndex = _IndexOf(vm, o => o.Path == @"C:\recent");

        Assert.True(sepIndex > favIndex);
        Assert.True(sepIndex < recentIndex);
        Assert.False(vm.RememberedPaths.Single(o => o.IsSeparator).IsSelectable);
    }

    [Fact]
    public async Task RememberedPaths_WithOnlyRecents_HasNoSeparator()
    {
        var vm = NewVmWithWorkingPaths(new WorkingPathHistory([@"C:\recent"], []), out _);

        await vm.LoadAsync();

        Assert.DoesNotContain(vm.RememberedPaths, o => o.IsSeparator);
    }

    [Fact]
    public async Task RemoveRememberedPath_ForgetsItThroughTheStoreAndDropsItFromTheList()
    {
        var vm = NewVmWithWorkingPaths(new WorkingPathHistory([@"C:\a", @"C:\b"], []), out var store);
        store.RemoveAsync(@"C:\a", Arg.Any<CancellationToken>()).Returns(new WorkingPathHistory([@"C:\b"], []));
        await vm.LoadAsync();

        var target = vm.RememberedPaths.Single(o => o.Path == @"C:\a");
        await vm.RemoveRememberedPathCommand.ExecuteAsync(target);

        await store.Received(1).RemoveAsync(@"C:\a", Arg.Any<CancellationToken>());
        Assert.DoesNotContain(vm.RememberedPaths, o => o.Path == @"C:\a");
    }

    [Fact]
    public async Task RemoveRememberedPath_IgnoresTheCloneActionAndSeparator()
    {
        var vm = NewVmWithWorkingPaths(new WorkingPathHistory([@"C:\recent"], [@"C:\fav"]), out var store);
        await vm.LoadAsync();

        await vm.RemoveRememberedPathCommand.ExecuteAsync(vm.RememberedPaths.Single(o => o.IsCloneAction));
        await vm.RemoveRememberedPathCommand.ExecuteAsync(vm.RememberedPaths.Single(o => o.IsSeparator));

        await store.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static int _IndexOf(NewSessionDialogViewModel vm, Func<RememberedPathOption, bool> predicate)
    {
        for (var i = 0; i < vm.RememberedPaths.Count; i++)
        {
            if (predicate(vm.RememberedPaths[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static NewSessionDialogViewModel NewVmWithWorkingPaths(WorkingPathHistory history, out IWorkingPathHistoryStore workingPathStore)
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<SessionProfile> { profile });
        var loginChecker = Substitute.For<IProfileLoginChecker>();
        loginChecker.IsLoggedIn(profile).Returns(true);

        workingPathStore = Substitute.For<IWorkingPathHistoryStore>();
        workingPathStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(history);

        return new NewSessionDialogViewModel(store, loginChecker, mcpServerCatalog: null, workingPathStore: workingPathStore);
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
        var loginChecker = Substitute.For<IProfileLoginChecker>();
        foreach (var profile in profiles)
        {
            loginChecker.IsLoggedIn(profile).Returns(true);
        }

        return new NewSessionDialogViewModel(
            store, loginChecker, mcpServerCatalog: null, workingPathStore: null, conversationPickers: null,
            ttyProviderResolver: null, ttyProviderRegistry: null, sessionProviderRegistry);
    }

    [Fact]
    public async Task InitialPrompt_WhenAPrefillCarriesOne_IsShownForTheOperatorToRead()
    {
        var vm = NewVm(out _, new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work")));
        await vm.LoadAsync();

        Assert.False(vm.HasInitialPrompt);

        vm.InitialPrompt = "## Crash on login\r\nSteps to reproduce...";

        // The dialog is the gate a prefill passes through, so the field that can hold text written outside the
        // cockpit has to be on it — otherwise the operator confirms a prompt they were never shown.
        Assert.True(vm.HasInitialPrompt);
        Assert.Equal("## Crash on login\r\nSteps to reproduce...", vm.InitialPrompt);
    }

    [Fact]
    public async Task InitialPrompt_WhenBlank_LeavesTheRowHidden()
    {
        var vm = NewVm(out _, new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work")));
        await vm.LoadAsync();

        vm.InitialPrompt = "   ";

        Assert.False(vm.HasInitialPrompt);
    }

    private static NewSessionDialogViewModel NewVm(out IProfileLoginChecker loginChecker, params SessionProfile[] profiles)
    {
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(profiles.ToList());
        loginChecker = Substitute.For<IProfileLoginChecker>();
        foreach (var profile in profiles)
        {
            loginChecker.IsLoggedIn(profile).Returns(true);
        }

        return new NewSessionDialogViewModel(store, loginChecker);
    }

    private static NewSessionDialogViewModel NewVmWithTty(
        out IProfileLoginChecker loginChecker,
        SessionProfile[] profiles,
        ITtySessionProviderResolver ttyProviderResolver,
        IPluginTtyProviderRegistry ttyProviderRegistry)
    {
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(profiles.ToList());
        loginChecker = Substitute.For<IProfileLoginChecker>();
        foreach (var profile in profiles)
        {
            loginChecker.IsLoggedIn(profile).Returns(true);
        }

        return new NewSessionDialogViewModel(
            store, loginChecker, mcpServerCatalog: null, workingPathStore: null, conversationPickers: null,
            ttyProviderResolver, ttyProviderRegistry);
    }

    private static NewSessionDialogViewModel NewVmWithMcp(
        out IProfileLoginChecker loginChecker,
        SessionProfile[] profiles,
        params McpServerConfig[] registry)
    {
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(profiles.ToList());
        loginChecker = Substitute.For<IProfileLoginChecker>();
        foreach (var profile in profiles)
        {
            loginChecker.IsLoggedIn(profile).Returns(true);
        }

        var mcpServerCatalog = Substitute.For<IMcpServerCatalog>();
        // The dialog asks per project (AC-163) — with no project selected that is the plain catalog, which is what
        // the real McpServerCatalog returns for a null id.
        mcpServerCatalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(registry.ToList());
        mcpServerCatalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns(registry.ToList());

        return new NewSessionDialogViewModel(store, loginChecker, mcpServerCatalog);
    }
}
