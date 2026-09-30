using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
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
    public void PermissionModes_IncludeBypass_SinceTheDialogIsWhereItCanBeChosen()
    {
        var vm = NewVm(out _);

        Assert.Contains("bypassPermissions", vm.PermissionModes.Select(mode => mode.Value));
    }

    // --- AC-130: per-profile default working directory + MCP pre-selection ---

    // --- AC-131: managing the remembered-folders quick-pick ---

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

}
