using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
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

        Assert.Single(vm.Profiles);
        var row = vm.Profiles[0];
        Assert.Equal("work", row.Label);
        Assert.Equal("/home/r/.claude-work", row.ConfigDir);
        // The per-profile permission/model/effort defaults are read generically from OptionDefaults now (covered by
        // EditableProfileViewModelPluginProviderTests), not the retired typed selections — this covers the row mapping
        // and login status.
        Assert.True(row.IsLoggedIn);
        Assert.Equal(row, vm.SelectedProfile);
    }

    [Fact]
    public async Task LoadAsync_ExcludesInternalEndpoints_FromTheProfileMcpPreselection()
    {
        var work = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns([work]);
        var loginChecker = Substitute.For<IProfileLoginChecker>();
        loginChecker.IsLoggedIn(work).Returns(true);

        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>
        {
            new() { Name = "server-a", Command = "npx" },
            // An internal-only endpoint (AC-204, the Autopilot CEO/step tools) is enabled and mountable but must not
            // be offered as a profile pre-selection. Red without the fix, which listed every enabled catalog server.
            new() { Name = "cockpit-autopilot-ceo", Url = "http://127.0.0.1:1/mcp", Internal = true },
        });

        var vm = new ManageProfilesDialogViewModel(store, loginChecker, mcpServerCatalog: catalog);

        await vm.LoadAsync();

        Assert.Equal(new[] { "server-a" }, vm.Profiles[0].McpServers.Select(server => server.Name));
    }

    [Fact]
    public void AddProfile_AppendsANewEditableRowAndSelectsIt()
    {
        var vm = new ManageProfilesDialogViewModel(Substitute.For<ISessionProfileStore>(), Substitute.For<IProfileLoginChecker>());

        vm.AddProfileCommand.Execute(null);

        Assert.Single(vm.Profiles);
        Assert.Equal(vm.Profiles[0], vm.SelectedProfile);
        Assert.True(vm.RemoveProfileCommand.CanExecute(null));
    }

    [Fact]
    public void RemoveProfile_AsksForConfirmationWithoutDroppingTheRowYet()
    {
        var vm = new ManageProfilesDialogViewModel(Substitute.For<ISessionProfileStore>(), Substitute.For<IProfileLoginChecker>());
        vm.AddProfileCommand.Execute(null);
        var target = vm.SelectedProfile;

        vm.RemoveProfileCommand.Execute(null);

        Assert.True(vm.IsConfirmingRemove);
        Assert.Equal(target!.Label, vm.PendingRemovalLabel);
        Assert.Contains(target, vm.Profiles); // not dropped until confirmed
    }

    [Fact]
    public async Task ConfirmRemove_DropsTheRowWithoutPersistingUntilSave()
    {
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new SessionProfile("default", new OllamaConfig("http://localhost:11434", "llama3.1")),
            new SessionProfile("personal", new OllamaConfig("http://localhost:11434", "llama3.1")),
        ]);
        var vm = new ManageProfilesDialogViewModel(store, Substitute.For<IProfileLoginChecker>());
        await vm.LoadAsync();
        vm.SelectedProfile = vm.Profiles.Single(p => p.Label == "default");
        vm.RemoveProfileCommand.Execute(null);

        vm.ConfirmRemoveCommand.Execute(null);

        Assert.False(vm.IsConfirmingRemove);
        Assert.Equal("personal", Assert.Single(vm.Profiles).Label);
        await store.DidNotReceive().SaveAsync(Arg.Any<IReadOnlyList<SessionProfile>>(), Arg.Any<CancellationToken>());

        await vm.SaveCommand.ExecuteAsync(null);

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
        vm.ConfirmRemoveCommand.Execute(null);

        await vm.SaveCommand.ExecuteAsync(null);

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

        Assert.False(vm.IsConfirmingRemove);
        Assert.Contains(target!, vm.Profiles);
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
        Assert.True(closed);
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

        Assert.True(Assert.Single(vm.Profiles).AutoApproveTools);
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

    // The profile's spawn environment variables (AC-22): rows load and save through the editable row VM, an
    // invalid or duplicate key gates the save, and the editor only shows for a provider that declares the
    // SupportsEnvVars capability.
    [Fact]
    public void ToProfile_CarriesTheEnvironmentVariableRows_IncludingTheSecretFlag()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"))
        {
            EnvironmentVariables = [new ProfileEnvironmentVariable("AI_OS_ROOT", "/home/raymond/AI-OS")],
        };
        var row = new EditableProfileViewModel(profile, isLoggedIn: true);
        row.EnvironmentVariables.Add(new ProfileEnvironmentVariableViewModel("MY_TOKEN", "s3cret", isSecret: true));

        var saved = row.ToProfile();

        Assert.Equal(
            new[]
            {
                new ProfileEnvironmentVariable("AI_OS_ROOT", "/home/raymond/AI-OS"),
                new ProfileEnvironmentVariable("MY_TOKEN", "s3cret", IsSecret: true),
            },
            saved.EnvironmentVariables);
    }

    // The env-row gates are proven on a profile that is otherwise valid (an Ollama profile with base URL and
    // model filled), so IsValid flips on the rows alone — a legacy ClaudeConfig profile resolves to the Ollama
    // fallback with empty fields and is invalid regardless, which would make these assertions prove nothing.
    private static EditableProfileViewModel _ValidLocalRow() => new(
        new SessionProfile("local", new OllamaConfig("http://localhost:11434", "llama3.1", null)), isLoggedIn: true);

    [Theory]
    [InlineData("2INVALID")]
    [InlineData("")]
    public void IsValid_AnEnvironmentVariableWithAnUnsettableName_GatesTheSave(string key)
    {
        var row = _ValidLocalRow();
        Assert.True(row.IsValid, "the gate below must be attributable to the row, not to an incomplete profile");
        row.EnvironmentVariables.Add(new ProfileEnvironmentVariableViewModel(key, "value"));

        Assert.False(row.IsValid);
    }

    [Fact]
    public void IsValid_ADuplicateEnvironmentVariableKey_GatesTheSave()
    {
        var row = _ValidLocalRow();
        row.EnvironmentVariables.Add(new ProfileEnvironmentVariableViewModel("AI_OS_ROOT", "/first"));
        row.EnvironmentVariables.Add(new ProfileEnvironmentVariableViewModel("AI_OS_ROOT", "/second"));

        Assert.False(row.IsValid);
    }

    // The spawn composition folds case-insensitively (TtyEnvironment, the Claude driver), so two case-variant
    // rows are one variable at spawn and one value would silently win — the save gate must catch them as the
    // duplicate they effectively are.
    [Fact]
    public void IsValid_ACaseVariantDuplicateEnvironmentVariableKey_GatesTheSave()
    {
        var row = _ValidLocalRow();
        row.EnvironmentVariables.Add(new ProfileEnvironmentVariableViewModel("MyVar", "/first"));
        row.EnvironmentVariables.Add(new ProfileEnvironmentVariableViewModel("MYVAR", "/second"));

        Assert.False(row.IsValid);
    }

    [Fact]
    public void SupportsEnvVars_FollowsThePluginProvidersDeclaredCapability()
    {
        var registry = Substitute.For<IPluginProviderRegistry>();
        registry.Resolve("env-capable").Returns(_Registration("env-capable", supportsEnvVars: true));
        registry.Resolve("env-less").Returns(_Registration("env-less", supportsEnvVars: false));

        var capable = new EditableProfileViewModel(
            new SessionProfile("a", new PluginProviderConfig("env-capable", "{}")), isLoggedIn: true, pluginProviderRegistry: registry);
        var incapable = new EditableProfileViewModel(
            new SessionProfile("b", new PluginProviderConfig("env-less", "{}")), isLoggedIn: true, pluginProviderRegistry: registry);

        Assert.True(capable.SupportsEnvVars);
        Assert.False(incapable.SupportsEnvVars);
    }

    [Fact]
    public void SupportsEnvVars_IsFalseForAnHttpProvider_WhichSpawnsNothingToInjectInto()
    {
        var row = new EditableProfileViewModel(
            new SessionProfile("local", new OllamaConfig("http://localhost:11434", "llama3.1", null)), isLoggedIn: true);

        Assert.False(row.SupportsEnvVars);
    }

    private static SessionProviderRegistration _Registration(string providerId, bool supportsEnvVars) => new(
        ProviderId: providerId,
        DisplayName: providerId,
        CreateDriverFactory: _ => null!,
        Capabilities: new PluginSessionCapabilities(true, true) { SupportsEnvVars = supportsEnvVars },
        CreateConfigView: _ => null!);

    [Fact]
    public async Task Save_WithAnEmptyConfigDir_DoesNotPersistAndReportsIt()
    {
        var store = Substitute.For<ISessionProfileStore>();
        var vm = new ManageProfilesDialogViewModel(store, Substitute.For<IProfileLoginChecker>());
        vm.AddProfileCommand.Execute(null); // seeds a "new profile" with an empty config directory

        await vm.SaveCommand.ExecuteAsync(null);

        await store.DidNotReceive().SaveAsync(Arg.Any<IReadOnlyList<SessionProfile>>(), Arg.Any<CancellationToken>());
        Assert.False(string.IsNullOrEmpty(vm.StatusMessage));
    }

    [Fact]
    public void AddProfile_LetsTheNewRowChooseItsProvider_ButLoadedRowsCannot()
    {
        var store = Substitute.For<ISessionProfileStore>();
        var vm = new ManageProfilesDialogViewModel(store, Substitute.For<IProfileLoginChecker>());

        vm.AddProfileCommand.Execute(null);

        Assert.True(vm.Profiles[0].CanChooseProvider);
    }

    [Fact]
    public async Task LoadAsync_ExistingProfilesCannotChangeProvider()
    {
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns([new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"))]);
        var vm = new ManageProfilesDialogViewModel(store, Substitute.For<IProfileLoginChecker>());

        await vm.LoadAsync();

        Assert.False(vm.Profiles[0].CanChooseProvider);
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
        Assert.False(string.IsNullOrEmpty(vm.StatusMessage));
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

        Assert.Equal(new[] { "llama3.1", "qwen2.5-7b-instruct" }, row.AvailableModels);
        Assert.Contains("2", vm.ModelFetchStatus);
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

        Assert.Null(profile.Purpose);
        Assert.NotNull(profile.Defaults);
    }
}
