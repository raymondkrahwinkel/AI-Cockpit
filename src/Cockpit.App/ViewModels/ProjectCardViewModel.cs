using Cockpit.Core.Projects;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One project as the Projects workspace list shows it (AC-618): the project itself plus a one-line origin badge —
/// "● This machine" or "◆ &lt;connection&gt;" — that replaces AC-245's separate "On this machine" heading over
/// every bound project. See <see cref="ProjectsViewModel"/>'s own remarks on where <see cref="OriginBadge"/> comes
/// from.
/// </summary>
public sealed record ProjectCardViewModel(Project Project, string OriginBadge);
