using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Profiles;
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
        var work = new ClaudeProfile("work", "/home/r/.claude-work");
        var personal = new ClaudeProfile("personal", "/home/r/.claude-personal");
        var vm = NewVm(out _, work, personal);

        await vm.LoadAsync();

        vm.Profiles.Should().Equal(work, personal);
        vm.SelectedProfile.Should().Be(work);
    }

    [Fact]
    public async Task SelectingProfile_LoadsItsSavedDefaults()
    {
        var profile = new ClaudeProfile("work", "/home/r/.claude-work",
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
        var profile = new ClaudeProfile("work", "/home/r/.claude-work");
        var vm = NewVm(out _, profile);
        await vm.LoadAsync();

        vm.SelectedPermissionMode.Should().Be(SessionOptionCatalog.DefaultPermissionMode);
        vm.SelectedModel.Should().Be(SessionOptionCatalog.DefaultModel);
        vm.SelectedEffort.Should().Be(SessionOptionCatalog.DefaultEffort);
    }

    [Fact]
    public async Task CanStart_IsFalseWhenTheSelectedProfileIsNotLoggedIn()
    {
        var profile = new ClaudeProfile("work", "/home/r/.claude-work");
        var vm = NewVm(out var loginChecker, profile);
        loginChecker.IsLoggedIn(profile).Returns(false);

        await vm.LoadAsync();

        vm.IsSelectedProfileLoggedIn.Should().BeFalse();
        vm.CanStart.Should().BeFalse();
        vm.ConfirmCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Confirm_RaisesCloseWithTheChosenProfileAndOptions()
    {
        var profile = new ClaudeProfile("work", "/home/r/.claude-work");
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
        NewSessionResult? result = new(new ClaudeProfile("x", "y"), SessionOptionCatalog.DefaultPermissionMode,
            SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort, null);
        var closed = false;
        vm.CloseRequested += r => { result = r; closed = true; };

        vm.CancelCommand.Execute(null);

        closed.Should().BeTrue();
        result.Should().BeNull();
    }

    [Fact]
    public void SdkAndTtyKind_BothShowSessionOptions_SinceTtyNowPassesThemAsLaunchOnlyStartDefaults()
    {
        var sdk = new NewSessionDialogViewModel(Substitute.For<IClaudeProfileStore>(), Substitute.For<IClaudeProfileLoginChecker>(), SessionKind.Sdk);
        var tty = new NewSessionDialogViewModel(Substitute.For<IClaudeProfileStore>(), Substitute.For<IClaudeProfileLoginChecker>(), SessionKind.Tty);

        sdk.ShowSessionOptions.Should().BeTrue();
        tty.ShowSessionOptions.Should().BeTrue();
    }

    [Fact]
    public void PermissionModes_IncludeBypass_SinceTheDialogIsWhereItCanBeChosen()
    {
        var vm = NewVm(out _);

        vm.PermissionModes.Select(mode => mode.Value).Should().Contain("bypassPermissions");
    }

    private static NewSessionDialogViewModel NewVm(out IClaudeProfileLoginChecker loginChecker, params ClaudeProfile[] profiles)
    {
        var store = Substitute.For<IClaudeProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(profiles.ToList());
        loginChecker = Substitute.For<IClaudeProfileLoginChecker>();
        foreach (var profile in profiles)
        {
            loginChecker.IsLoggedIn(profile).Returns(true);
        }

        return new NewSessionDialogViewModel(store, loginChecker, SessionKind.Sdk);
    }
}
