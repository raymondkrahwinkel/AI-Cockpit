using System.Windows.Input;

namespace Cockpit.App.ViewModels;

// What a project card can do, handed to every card `ProjectsViewModel` builds (AC-772).
//
// The commands themselves live on `CockpitViewModel` — starting a session is its job, not the project list's — but a
// card that reaches for them through `$parent[UserControl]` can only be drawn inside the view that owns that view
// model. That is how the Projects workspace and the Manage-projects window ended up with two independent copies of
// the same card markup, and why a language fix had to be made twice. Carrying the commands on the card instead lets
// one `ProjectCardView` render in either window, bound to nothing but its own data context.
//
// Null on a card built by the previewer or a test: every button then simply has no command and does nothing, which
// is what those contexts want anyway.
public sealed record ProjectCardActions(
    ICommand Start,
    ICommand StartWithOptions,
    ICommand Edit,
    ICommand OpenFolder,
    ICommand ToggleSharing);
