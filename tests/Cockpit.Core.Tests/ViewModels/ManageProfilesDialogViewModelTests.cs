using Cockpit.App.ViewModels;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The Manage-profiles dialog logic (#12/#17): loading profiles into editable rows, add/remove, and
/// persisting the edited list (including each profile's start defaults) through the store on save.
/// </summary>
public class ManageProfilesDialogViewModelTests
{
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
