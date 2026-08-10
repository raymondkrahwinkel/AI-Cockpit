using Cockpit.Core.Projects;

namespace Cockpit.App.ViewModels;

// One project as the Projects workspace list shows it (AC-618): the project itself plus a one-line origin badge —
// "● This machine" or "◆ &lt;connection&gt;" — that replaces AC-245's separate "On this machine" heading over
// every bound project. See `ProjectsViewModel`'s own remarks on where `OriginBadge` comes
// from.
public sealed record ProjectCardViewModel(Project Project, string OriginBadge)
{
    // AC-620: whether the launcher's own pill badge should show at all — never for "● This machine" (a local
    // project says nothing a launcher card needs to add), only for a bound one.
    public bool IsShared => OriginBadge.StartsWith('◆');
}
