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
        viewModel.ManageProfilesRequested += async () =>
        {
            await ShowManageProfilesAsync(dialog);
            await viewModel.LoadAsync();
        };

        return await dialog.ShowDialog<NewSessionResult?>(owner);
    }

    private async Task ShowManageProfilesAsync(Window owner)
    {
        var viewModel = new ManageProfilesDialogViewModel(_profileStore, _loginChecker);
        await viewModel.LoadAsync();

        var dialog = new ManageProfilesDialog { DataContext = viewModel };
        await dialog.ShowDialog(owner);
    }
}
