using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
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

}
