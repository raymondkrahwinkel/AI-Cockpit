using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The Manage-profiles dialog logic (#12/#17): loading profiles into editable rows, add/remove, and
/// persisting the edited list (including each profile's start defaults) through the store on save.
/// </summary>
public class ManageProfilesDialogViewModelTests
{

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
        // AC-1992: one source with brains, so the shared picker replaces the free-text Assistant field.
        const string Brain = "depot:acme/zyra@depot.acme.example";
        var plugins = Substitute.For<IPluginRegistrations>();
        plugins.MemorySourceRegistrations.Returns([new ProjectMemorySourceRegistration("depot", "Depot project", "Read it.")
        {
            ListBrainsAsync = _ => Task.FromResult(ProjectMemorySourceLocationsResult.Success([new("acme/zyra@depot.acme.example", "Zyra", "acme/zyra · Acme")])),
        }]);
        var store = Substitute.For<ISessionProfileStore>();
        var vm = new ManageProfilesDialogViewModel(store, Substitute.For<IProfileLoginChecker>(), pluginProviderRegistry: plugins);
        vm.AddProfileCommand.Execute(null);
        var row = vm.SelectedProfile!;
        row.Label = "ollama";
        row.SelectedProvider = SessionProviderCatalog.Resolve(SessionProvider.Ollama);
        row.BaseUrl = "http://localhost:11434";
        row.Model = "llama3.1";
        row.AutoApproveTools = true;
        Assert.True(row.BrainPicker.IsAvailable);
        await row.BrainPicker.LoadAsync();
        row.BrainPicker.Selected = row.BrainPicker.Choices.Single(choice => choice.Reference == Brain);

        await vm.SaveCommand.ExecuteAsync(null);

        await store.Received(1).SaveAsync(
            Arg.Is<IReadOnlyList<SessionProfile>>(list =>
                list.Count == 1 &&
                _AutoApprovesTools(list[0]) &&
                list[0].Assistant == Brain),
            Arg.Any<CancellationToken>());

        // A legacy name the list lacks stays, marked unavailable, through a load and an unrelated edit; None clears it.
        var legacy = new EditableProfileViewModel(new SessionProfile("work", new ClaudeConfig("/c")) { Assistant = "Zyra" }, isLoggedIn: true, pluginProviderRegistry: plugins);
        await legacy.BrainPicker.LoadAsync();
        legacy.Purpose = "unrelated";
        Assert.Equal("Zyra", legacy.ToProfile().Assistant);
        Assert.Equal(new BrainChoice("Zyra", "Zyra", "Not available here"), legacy.BrainPicker.Selected);
        legacy.BrainPicker.Selected = BrainPickerViewModel.None;
        Assert.Null(legacy.ToProfile().Assistant);
        Assert.False(new EditableProfileViewModel(new SessionProfile("bare", new ClaudeConfig("/c")), isLoggedIn: true).BrainPicker.IsAvailable);
    }

    private static bool _AutoApprovesTools(SessionProfile profile) => profile.Defaults is { AutoApproveTools: true };

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

}
