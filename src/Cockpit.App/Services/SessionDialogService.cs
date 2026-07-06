using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Profiles;

namespace Cockpit.App.Services;

/// <summary>
/// Hosts the modal dialogs over the main window. Constructs each dialog's view model with the profile
/// store/login checker it injects, so the dialogs get their data without a service locator, then shows
/// it with <c>ShowDialog</c> and relays the typed result back to the caller.
/// </summary>
public sealed class SessionDialogService : ISessionDialogService, ISingletonService
{
    private readonly IClaudeProfileStore _profileStore;
    private readonly IClaudeProfileLoginChecker _loginChecker;

    public SessionDialogService(IClaudeProfileStore profileStore, IClaudeProfileLoginChecker loginChecker)
    {
        _profileStore = profileStore;
        _loginChecker = loginChecker;
    }

    public async Task<NewSessionResult?> ShowNewSessionDialogAsync(SessionKind kind)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return null;
        }

        var viewModel = new NewSessionDialogViewModel(_profileStore, _loginChecker, kind);
        await viewModel.LoadAsync();

        var dialog = new NewSessionDialog { DataContext = viewModel };

        // Managing profiles from within the New-session dialog opens the Manage dialog over it, then
        // reloads the picker so any added/edited/removed profile (and its defaults) shows immediately.
        // async void via the Action event: guard it so a dialog/store failure can't tear the process
        // down — worst case the picker just doesn't refresh.
        viewModel.ManageProfilesRequested += async () =>
        {
            try
            {
                await ShowManageProfilesAsync(dialog);
                await viewModel.LoadAsync();
            }
            catch
            {
                // Managing profiles is best-effort from here; a failure must not crash the app.
            }
        };

        return await dialog.ShowDialog<NewSessionResult?>(owner);
    }

    public async Task ShowManageProfilesDialogAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            await ShowManageProfilesAsync(owner);
        }
    }

    private async Task ShowManageProfilesAsync(Window owner)
    {
        var viewModel = new ManageProfilesDialogViewModel(_profileStore, _loginChecker);
        await viewModel.LoadAsync();

        var dialog = new ManageProfilesDialog { DataContext = viewModel };
        await dialog.ShowDialog(owner);
    }
}
