using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Claude;
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
    public void RemoveProfile_AsksForConfirmationWithoutDroppingTheRowYet()
    {
        var vm = new ManageProfilesDialogViewModel(Substitute.For<IClaudeProfileStore>(), Substitute.For<IClaudeProfileLoginChecker>());
        vm.AddProfileCommand.Execute(null);
        var target = vm.SelectedProfile;

        vm.RemoveProfileCommand.Execute(null);

        vm.IsConfirmingRemove.Should().BeTrue();
        vm.PendingRemovalLabel.Should().Be(target!.Label);
        vm.Profiles.Should().Contain(target); // not dropped until confirmed
    }

    [Fact]
    public async Task ConfirmRemove_DropsTheRowAndPersistsImmediately()
    {
        var store = Substitute.For<IClaudeProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new ClaudeProfile("default", "/home/r/.claude"),
            new ClaudeProfile("personal", "/home/r/.claude-personal"),
        ]);
        var vm = new ManageProfilesDialogViewModel(store, Substitute.For<IClaudeProfileLoginChecker>());
        await vm.LoadAsync();
        vm.SelectedProfile = vm.Profiles.Single(p => p.Label == "default");
        vm.RemoveProfileCommand.Execute(null);

        await vm.ConfirmRemoveCommand.ExecuteAsync(null);

        vm.IsConfirmingRemove.Should().BeFalse();
        vm.Profiles.Should().ContainSingle().Which.Label.Should().Be("personal");
        await store.Received(1).SaveAsync(
            Arg.Is<IReadOnlyList<ClaudeProfile>>(list => list.Count == 1 && list[0].Label == "personal"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void CancelRemove_KeepsTheRow()
    {
        var vm = new ManageProfilesDialogViewModel(Substitute.For<IClaudeProfileStore>(), Substitute.For<IClaudeProfileLoginChecker>());
        vm.AddProfileCommand.Execute(null);
        var target = vm.SelectedProfile;
        vm.RemoveProfileCommand.Execute(null);

        vm.CancelRemoveCommand.Execute(null);

        vm.IsConfirmingRemove.Should().BeFalse();
        vm.Profiles.Should().Contain(target!);
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
    public async Task Save_WithAnEmptyConfigDir_DoesNotPersistAndReportsIt()
    {
        var store = Substitute.For<IClaudeProfileStore>();
        var vm = new ManageProfilesDialogViewModel(store, Substitute.For<IClaudeProfileLoginChecker>());
        vm.AddProfileCommand.Execute(null); // seeds a "new profile" with an empty config directory

        await vm.SaveCommand.ExecuteAsync(null);

        await store.DidNotReceive().SaveAsync(Arg.Any<IReadOnlyList<ClaudeProfile>>(), Arg.Any<CancellationToken>());
        vm.StatusMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void AddProfile_LetsTheNewRowChooseItsProvider_ButLoadedRowsCannot()
    {
        var store = Substitute.For<IClaudeProfileStore>();
        var vm = new ManageProfilesDialogViewModel(store, Substitute.For<IClaudeProfileLoginChecker>());

        vm.AddProfileCommand.Execute(null);

        vm.Profiles[0].CanChooseProvider.Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_ExistingProfilesCannotChangeProvider()
    {
        var store = Substitute.For<IClaudeProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns([new ClaudeProfile("work", "/home/r/.claude-work")]);
        var vm = new ManageProfilesDialogViewModel(store, Substitute.For<IClaudeProfileLoginChecker>());

        await vm.LoadAsync();

        vm.Profiles[0].CanChooseProvider.Should().BeFalse();
    }

    [Fact]
    public async Task Save_LocalProviderProfile_PersistsItsProviderConfig()
    {
        var store = Substitute.For<IClaudeProfileStore>();
        var vm = new ManageProfilesDialogViewModel(store, Substitute.For<IClaudeProfileLoginChecker>());
        vm.AddProfileCommand.Execute(null);
        var row = vm.SelectedProfile!;
        row.Label = "ollama";
        row.SelectedProvider = SessionProviderCatalog.Resolve(SessionProvider.Ollama);
        row.BaseUrl = "http://localhost:11434";
        row.Model = "llama3.1";

        await vm.SaveCommand.ExecuteAsync(null);

        await store.Received(1).SaveAsync(
            Arg.Is<IReadOnlyList<ClaudeProfile>>(list =>
                list.Count == 1 &&
                list[0].Provider == SessionProvider.Ollama &&
                ((OllamaConfig)list[0].ProviderConfig!).Model == "llama3.1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_LocalProviderWithoutAModel_DoesNotPersist()
    {
        var store = Substitute.For<IClaudeProfileStore>();
        var vm = new ManageProfilesDialogViewModel(store, Substitute.For<IClaudeProfileLoginChecker>());
        vm.AddProfileCommand.Execute(null);
        var row = vm.SelectedProfile!;
        row.Label = "ollama";
        row.SelectedProvider = SessionProviderCatalog.Resolve(SessionProvider.Ollama); // base URL auto-fills, model stays empty

        await vm.SaveCommand.ExecuteAsync(null);

        await store.DidNotReceive().SaveAsync(Arg.Any<IReadOnlyList<ClaudeProfile>>(), Arg.Any<CancellationToken>());
        vm.StatusMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RefreshModels_PopulatesTheSelectedLocalProfilesAvailableModels()
    {
        var catalog = Substitute.For<IModelCatalog>();
        catalog.ListModelsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { "llama3.1", "qwen2.5-7b-instruct" });
        var vm = new ManageProfilesDialogViewModel(Substitute.For<IClaudeProfileStore>(), Substitute.For<IClaudeProfileLoginChecker>(), catalog);
        vm.AddProfileCommand.Execute(null);
        var row = vm.SelectedProfile!;
        row.SelectedProvider = SessionProviderCatalog.Resolve(SessionProvider.Ollama);

        await vm.RefreshModelsCommand.ExecuteAsync(null);

        row.AvailableModels.Should().Equal("llama3.1", "qwen2.5-7b-instruct");
        vm.ModelFetchStatus.Should().Contain("2");
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
