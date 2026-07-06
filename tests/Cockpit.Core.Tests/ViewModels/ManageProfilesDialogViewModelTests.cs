using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The Manage-profiles dialog logic (#12/#17): loading profiles into editable rows, add/remove, and
/// persisting the edited list (including each profile's start defaults) through the store on save.
/// </summary>
public class ManageProfilesDialogViewModelTests
{
    [Fact]
    public async Task LoadAsync_TurnsStoredProfilesIntoEditableRowsWithTheirLoginStatus()
    {
        var work = new ClaudeProfile("work", "/home/r/.claude-work",
            Defaults: new ProfileDefaults("plan", "opus", "high"));
        var store = Substitute.For<IClaudeProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns([work]);
        var loginChecker = Substitute.For<IClaudeProfileLoginChecker>();
        loginChecker.IsLoggedIn(work).Returns(true);
        var vm = new ManageProfilesDialogViewModel(store, loginChecker);

        await vm.LoadAsync();

        vm.Profiles.Should().ContainSingle();
        var row = vm.Profiles[0];
        row.Label.Should().Be("work");
        row.ConfigDir.Should().Be("/home/r/.claude-work");
        row.SelectedPermissionMode.Value.Should().Be("plan");
        row.SelectedModel.Value.Should().Be("opus");
        row.SelectedEffort.Value.Should().Be("high");
        row.IsLoggedIn.Should().BeTrue();
        vm.SelectedProfile.Should().Be(row);
    }

    [Fact]
    public void AddProfile_AppendsANewEditableRowAndSelectsIt()
    {
        var vm = new ManageProfilesDialogViewModel(Substitute.For<IClaudeProfileStore>(), Substitute.For<IClaudeProfileLoginChecker>());

        vm.AddProfileCommand.Execute(null);

        vm.Profiles.Should().ContainSingle();
        vm.SelectedProfile.Should().Be(vm.Profiles[0]);
        vm.RemoveProfileCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void RemoveProfile_DropsTheSelectedRow()
    {
        var vm = new ManageProfilesDialogViewModel(Substitute.For<IClaudeProfileStore>(), Substitute.For<IClaudeProfileLoginChecker>());
        vm.AddProfileCommand.Execute(null);
        vm.AddProfileCommand.Execute(null);
        var toRemove = vm.SelectedProfile;

        vm.RemoveProfileCommand.Execute(null);

        vm.Profiles.Should().NotContain(toRemove!);
        vm.Profiles.Should().ContainSingle();
    }

    [Fact]
    public async Task Save_PersistsTheEditedListWithDefaultsAndClosesTheDialog()
    {
        var store = Substitute.For<IClaudeProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns([new ClaudeProfile("work", "/home/r/.claude-work")]);
        var vm = new ManageProfilesDialogViewModel(store, Substitute.For<IClaudeProfileLoginChecker>());
        await vm.LoadAsync();
        vm.SelectedProfile!.Label = "work-renamed";
        vm.SelectedProfile.SelectedModel = new ModelOption("Opus 4.8", "opus");
        var closed = false;
        vm.CloseRequested += () => closed = true;

        await vm.SaveCommand.ExecuteAsync(null);

        await store.Received(1).SaveAsync(
            Arg.Is<IReadOnlyList<ClaudeProfile>>(list =>
                list.Count == 1 &&
                list[0].Label == "work-renamed" &&
                list[0].Defaults!.Model == "opus"),
            Arg.Any<CancellationToken>());
        closed.Should().BeTrue();
    }

    [Fact]
    public void ToProfile_CollapsesEmptyExecutableAndPurposeToNull()
    {
        var editable = new EditableProfileViewModel(new ClaudeProfile("work", "/home/r/.claude-work"), isLoggedIn: false)
        {
            ExecutablePath = "   ",
            Purpose = "",
        };

        var profile = editable.ToProfile();

        profile.ExecutablePath.Should().BeNull();
        profile.Purpose.Should().BeNull();
        profile.Defaults.Should().NotBeNull();
    }
}
