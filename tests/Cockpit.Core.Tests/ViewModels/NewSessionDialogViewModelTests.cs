using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
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
        var work = new SessionProfile("work", "/home/r/.claude-work");
        var personal = new SessionProfile("personal", "/home/r/.claude-personal");
        var vm = NewVm(out _, work, personal);

        await vm.LoadAsync();

        vm.Profiles.Should().Equal(work, personal);
        vm.SelectedProfile.Should().Be(work);
    }

    [Fact]
    public async Task SelectingProfile_LoadsItsSavedDefaults()
    {
        var profile = new SessionProfile("work", "/home/r/.claude-work",
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
        var profile = new SessionProfile("work", "/home/r/.claude-work");
        var vm = NewVm(out _, profile);
        await vm.LoadAsync();

        vm.SelectedPermissionMode.Should().Be(SessionOptionCatalog.DefaultPermissionMode);
        vm.SelectedModel.Should().Be(SessionOptionCatalog.DefaultModel);
        vm.SelectedEffort.Should().Be(SessionOptionCatalog.DefaultEffort);
    }

    [Fact]
    public async Task CanStart_IsFalseForSdkWhenTheSelectedProfileIsNotLoggedIn()
    {
        var profile = new SessionProfile("work", "/home/r/.claude-work");
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
        var profile = new SessionProfile("work", "/home/r/.claude-work");
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
        var profile = new SessionProfile("work", "/home/r/.claude-work");
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
        var local = new SessionProfile("ollama", string.Empty,
            ProviderConfig: new OllamaConfig("http://localhost:11434", "llama3.1"));
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
        var local = new SessionProfile("ollama", string.Empty,
            ProviderConfig: new OllamaConfig("http://localhost:11434", "llama3.1"));
        var vm = NewVm(out _, local);
        vm.SelectTtyCommand.Execute(null);

        await vm.LoadAsync();

        vm.SelectedKind.Should().Be(SessionKind.Sdk);
    }

    [Fact]
    public async Task Confirm_RaisesCloseWithTheChosenProfileAndOptions()
    {
        var profile = new SessionProfile("work", "/home/r/.claude-work");
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
        NewSessionResult? result = new(SessionKind.Sdk, new SessionProfile("x", "y"), SessionOptionCatalog.DefaultPermissionMode,
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
        var profile = new SessionProfile("work", "/home/r/.claude-work");
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
        var profile = new SessionProfile("work", "/home/r/.claude-work");
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
        var profile = new SessionProfile("work", "/home/r/.claude-work");
        var vm = NewVmWithMcp(out _, [profile],
            new McpServerConfig { Name = "on" },
            new McpServerConfig { Name = "off", Enabled = false });

        await vm.LoadAsync();

        vm.McpServers.Select(server => server.Name).Should().Equal("on");
    }

    [Fact]
    public async Task LoadAsync_WithNoRegistryServers_HasMcpServersIsFalse()
    {
        var profile = new SessionProfile("work", "/home/r/.claude-work");
        var vm = NewVmWithMcp(out _, [profile]);

        await vm.LoadAsync();

        vm.HasMcpServers.Should().BeFalse();
        vm.McpServers.Should().BeEmpty();
    }

    [Fact]
    public async Task Confirm_WithAnUncheckedMcpServer_ExcludesItFromTheResult()
    {
        var profile = new SessionProfile("work", "/home/r/.claude-work");
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
        var profile = new SessionProfile("work", "/home/r/.claude-work");
        var vm = NewVmWithMcp(out var loginChecker, [profile]);
        loginChecker.IsLoggedIn(profile).Returns(true);
        await vm.LoadAsync();

        NewSessionResult? result = null;
        vm.CloseRequested += r => result = r;
        vm.ConfirmCommand.Execute(null);

        result.Should().NotBeNull();
        result!.EnabledMcpServerNames.Should().BeNull();
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
