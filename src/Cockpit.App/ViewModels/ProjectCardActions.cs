using System.Windows.Input;

namespace Cockpit.App.ViewModels;

// AC-772: the commands travel with the card so `ProjectCardView` binds to its own data context, which is what lets
// one control render in both the Projects workspace and the Manage-projects window. Null under the previewer and in
// tests, where every button is simply inert.
public sealed record ProjectCardActions(
    ICommand Start,
    ICommand StartWithOptions,
    ICommand Edit,
    ICommand OpenFolder,
    ICommand ToggleSharing,
    ICommand SyncNow);
