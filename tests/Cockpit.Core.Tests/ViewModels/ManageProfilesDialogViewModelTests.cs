using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
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
        var work = new SessionProfile(
            "work",
            new ClaudeConfig("/home/r/.claude-work"),
            Defaults: new ProfileDefaults("plan", "opus", "high"));
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns([work]);
        var loginChecker = Substitute.For<IProfileLoginChecker>();
        loginChecker.IsLoggedIn(work).Returns(true);
        var vm = new ManageProfilesDialogViewModel(store, loginChecker);

        await vm.LoadAsync();

        vm.Profiles.Should().ContainSingle();
        var row = vm.Profiles[0];
        row.Label.Should().Be("work");
        row.ConfigDir.Should().Be("/home/r/.claude-work");
        // The per-profile permission/model/effort defaults are read generically from OptionDefaults now (covered by
        // EditableProfileViewModelPluginProviderTests), not the retired typed selections — this covers the row mapping
        // and login status.
        row.IsLoggedIn.Should().BeTrue();
        vm.SelectedProfile.Should().Be(row);
    }

    [Fact]
    public void AddProfile_AppendsANewEditableRowAndSelectsIt()
    {
        var vm = new ManageProfilesDialogViewModel(Substitute.For<ISessionProfileStore>(), Substitute.For<IProfileLoginChecker>());

        vm.AddProfileCommand.Execute(null);

        vm.Profiles.Should().ContainSingle();
        vm.SelectedProfile.Should().Be(vm.Profiles[0]);
        vm.RemoveProfileCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void RemoveProfile_AsksForConfirmationWithoutDroppingTheRowYet()
    {
        var vm = new ManageProfilesDialogViewModel(Substitute.For<ISessionProfileStore>(), Substitute.For<IProfileLoginChecker>());
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
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new SessionProfile("default", new ClaudeConfig("/home/r/.claude")),
            new SessionProfile("personal", new ClaudeConfig("/home/r/.claude-personal")),
        ]);
        var vm = new ManageProfilesDialogViewModel(store, Substitute.For<IProfileLoginChecker>());
        await vm.LoadAsync();
        vm.SelectedProfile = vm.Profiles.Single(p => p.Label == "default");
        vm.RemoveProfileCommand.Execute(null);

        await vm.ConfirmRemoveCommand.ExecuteAsync(null);

        vm.IsConfirmingRemove.Should().BeFalse();
        vm.Profiles.Should().ContainSingle().Which.Label.Should().Be("personal");
        await store.Received(1).SaveAsync(
            Arg.Is<IReadOnlyList<SessionProfile>>(list => list.Count == 1 && list[0].Label == "personal"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// #45 review finding 1: <c>ConfirmRemoveAsync</c> persists every remaining row through <c>ToProfile()</c>
    /// with no <c>IsValid</c> guard. Before the fix, an orphaned plugin profile (its provider plugin
    /// removed/disabled/failed to load) had a null <c>PluginConfigView</c>, so <c>ToProfile()</c> returned
    /// a bare <see cref="SessionProfile"/> with no <see cref="ProviderConfig"/> at all — removing some
    /// *other* profile silently rewrote the orphan row into a broken Claude profile, discarding its
    /// ProviderId/ConfigJson (and any API key inside). Confirming a removal of an unrelated row must leave
    /// the orphan's stored config completely untouched.
    /// </summary>
    [Fact]
    public async Task ConfirmRemove_WithAnOrphanedPluginProfileAmongTheRemainingRows_DoesNotCorruptItsProviderConfig()
    {
        var orphanConfig = new PluginProviderConfig("gemini-provider.gemini", """{"ApiKey":"super-secret"}""");
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new SessionProfile("orphaned-gemini", orphanConfig),
            new SessionProfile("personal", new ClaudeConfig("/home/r/.claude-personal")),
        ]);
        // An empty registry — the "gemini-provider.gemini" plugin is not registered, exactly the removed/
        // disabled/failed-to-load state the orphan row is stuck in.
        var vm = new ManageProfilesDialogViewModel(store, Substitute.For<IProfileLoginChecker>(), pluginProviderRegistry: new PluginProviderRegistry());
        await vm.LoadAsync();
        vm.SelectedProfile = vm.Profiles.Single(p => p.Label == "personal");
        vm.RemoveProfileCommand.Execute(null);

        await vm.ConfirmRemoveCommand.ExecuteAsync(null);

        await store.Received(1).SaveAsync(
            Arg.Is<IReadOnlyList<SessionProfile>>(list =>
                list.Count == 1 &&
                list[0].Label == "orphaned-gemini" &&
                list[0].ProviderConfig == orphanConfig),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void CancelRemove_KeepsTheRow()
    {
        var vm = new ManageProfilesDialogViewModel(Substitute.For<ISessionProfileStore>(), Substitute.For<IProfileLoginChecker>());
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
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns([new SessionProfile("local", new OllamaConfig("http://localhost:11434", "llama3.1"),
                Defaults: new ProfileDefaults("default", "sonnet", "medium", AutoApproveTools: true))]);
        var vm = new ManageProfilesDialogViewModel(store, Substitute.For<IProfileLoginChecker>());
        await vm.LoadAsync();
        vm.SelectedProfile!.Label = "local-renamed";
        var closed = false;
        vm.CloseRequested += () => closed = true;

        await vm.SaveCommand.ExecuteAsync(null);

        await store.Received(1).SaveAsync(
            Arg.Is<IReadOnlyList<SessionProfile>>(list =>
                list.Count == 1 &&
                list[0].Label == "local-renamed" &&
                list[0].Defaults!.AutoApproveTools),
            Arg.Any<CancellationToken>());
        closed.Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_TurnsAStoredAutoApproveToolsDefaultIntoTheEditableRow()
    {
        var work = new SessionProfile(
            "ollama",
            new OllamaConfig("http://localhost:11434", "llama3.1"),
            Defaults: new ProfileDefaults("default", "sonnet", "medium", AutoApproveTools: true));
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns([work]);
        var vm = new ManageProfilesDialogViewModel(store, Substitute.For<IProfileLoginChecker>());

        await vm.LoadAsync();

        vm.Profiles.Should().ContainSingle().Which.AutoApproveTools.Should().BeTrue();
    }

    [Fact]
    public async Task Save_PersistsTheAutoApproveToolsDefault()
    {
        var store = Substitute.For<ISessionProfileStore>();
        var vm = new ManageProfilesDialogViewModel(store, Substitute.For<IProfileLoginChecker>());
        vm.AddProfileCommand.Execute(null);
        var row = vm.SelectedProfile!;
        row.Label = "ollama";
        row.SelectedProvider = SessionProviderCatalog.Resolve(SessionProvider.Ollama);
        row.BaseUrl = "http://localhost:11434";
        row.Model = "llama3.1";
        row.AutoApproveTools = true;

        await vm.SaveCommand.ExecuteAsync(null);

        await store.Received(1).SaveAsync(
            Arg.Is<IReadOnlyList<SessionProfile>>(list =>
                list.Count == 1 &&
                list[0].Defaults!.AutoApproveTools),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_WithAnEmptyConfigDir_DoesNotPersistAndReportsIt()
    {
        var store = Substitute.For<ISessionProfileStore>();
        var vm = new ManageProfilesDialogViewModel(store, Substitute.For<IProfileLoginChecker>());
        vm.AddProfileCommand.Execute(null); // seeds a "new profile" with an empty config directory

        await vm.SaveCommand.ExecuteAsync(null);

        await store.DidNotReceive().SaveAsync(Arg.Any<IReadOnlyList<SessionProfile>>(), Arg.Any<CancellationToken>());
        vm.StatusMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void AddProfile_LetsTheNewRowChooseItsProvider_ButLoadedRowsCannot()
    {
        var store = Substitute.For<ISessionProfileStore>();
        var vm = new ManageProfilesDialogViewModel(store, Substitute.For<IProfileLoginChecker>());

        vm.AddProfileCommand.Execute(null);

        vm.Profiles[0].CanChooseProvider.Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_ExistingProfilesCannotChangeProvider()
    {
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns([new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"))]);
        var vm = new ManageProfilesDialogViewModel(store, Substitute.For<IProfileLoginChecker>());

        await vm.LoadAsync();

        vm.Profiles[0].CanChooseProvider.Should().BeFalse();
    }

    [Fact]
    public async Task Save_LocalProviderProfile_PersistsItsProviderConfig()
    {
        var store = Substitute.For<ISessionProfileStore>();
        var vm = new ManageProfilesDialogViewModel(store, Substitute.For<IProfileLoginChecker>());
        vm.AddProfileCommand.Execute(null);
        var row = vm.SelectedProfile!;
        row.Label = "ollama";
        row.SelectedProvider = SessionProviderCatalog.Resolve(SessionProvider.Ollama);
        row.BaseUrl = "http://localhost:11434";
        row.Model = "llama3.1";

        await vm.SaveCommand.ExecuteAsync(null);

        await store.Received(1).SaveAsync(
            Arg.Is<IReadOnlyList<SessionProfile>>(list =>
                list.Count == 1 &&
                list[0].Provider == SessionProvider.Ollama &&
                ((OllamaConfig)list[0].ProviderConfig!).Model == "llama3.1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_LocalProviderWithoutAModel_DoesNotPersist()
    {
        var store = Substitute.For<ISessionProfileStore>();
        var vm = new ManageProfilesDialogViewModel(store, Substitute.For<IProfileLoginChecker>());
        vm.AddProfileCommand.Execute(null);
        var row = vm.SelectedProfile!;
        row.Label = "ollama";
        row.SelectedProvider = SessionProviderCatalog.Resolve(SessionProvider.Ollama); // base URL auto-fills, model stays empty

        await vm.SaveCommand.ExecuteAsync(null);

        await store.DidNotReceive().SaveAsync(Arg.Any<IReadOnlyList<SessionProfile>>(), Arg.Any<CancellationToken>());
        vm.StatusMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RefreshModels_PopulatesTheSelectedLocalProfilesAvailableModels()
    {
        var catalog = Substitute.For<IModelCatalog>();
        catalog.ListModelsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { "llama3.1", "qwen2.5-7b-instruct" });
        var vm = new ManageProfilesDialogViewModel(Substitute.For<ISessionProfileStore>(), Substitute.For<IProfileLoginChecker>(), catalog);
        vm.AddProfileCommand.Execute(null);
        var row = vm.SelectedProfile!;
        row.SelectedProvider = SessionProviderCatalog.Resolve(SessionProvider.Ollama);

        await vm.RefreshModelsCommand.ExecuteAsync(null);

        row.AvailableModels.Should().Equal("llama3.1", "qwen2.5-7b-instruct");
        vm.ModelFetchStatus.Should().Contain("2");
    }

    [Fact]
    public void ToProfile_CollapsesEmptyPurposeToNull()
    {
        // The executable-path collapse is the Claude provider plugin's concern now (its config view); this covers the
        // provider-neutral Purpose collapse on a core provider.
        var editable = new EditableProfileViewModel(new SessionProfile("local", new OllamaConfig("http://localhost:11434", "llama3.1")), isLoggedIn: false)
        {
            Purpose = "   ",
        };

        var profile = editable.ToProfile();

        profile.Purpose.Should().BeNull();
        profile.Defaults.Should().NotBeNull();
    }
}
