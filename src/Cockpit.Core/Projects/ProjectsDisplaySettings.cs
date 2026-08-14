namespace Cockpit.Core.Projects;

// How the Projects page draws its projects (AC-772). Three shapes of the same page, not three feature sets: the
// wording, the actions and what a project shows are identical in each — only the arrangement differs, which is why
// this is one operator preference rather than three screens.
public enum ProjectsLayoutMode
{
    // The default: one card per project, wrapped. What the page has always looked like.
    Cards,

    // Wide rows. Roughly twice as many projects on a screen, at the cost of a smaller picture to recognise them by.
    List,

    // Sorted by what was worked on last, so the page answers "what was I doing" in one click. See
    // `ProjectsDisplaySettings.ContinueLayoutAvailable` for why it is not offered yet.
    Continue,
}

// The Projects page's own display preference, persisted under the `projects` section of `cockpit.json` (same store
// pattern as `LayoutSettings`). Deliberately not an Options page: the choice is made on the page it changes, where
// the operator can see what it does, and there is nothing else in it worth a settings screen.
public sealed record ProjectsDisplaySettings
{
    // AC-772 criterion 19: `Continue` leans entirely on `Project.LastOpenedAt`, and whether that is filled reliably
    // enough to order a list by has not been verified yet — a fresh install has none at all. Until it is, the
    // segment is not offered, and a stored choice of it falls back to `Cards` (see `Normalized`). A segment that
    // lands on an empty or wrong date is worse than a segment that is not there.
    public const bool ContinueLayoutAvailable = false;

    public ProjectsLayoutMode LayoutMode { get; init; } = ProjectsLayoutMode.Cards;

    // The mode this cockpit can actually draw right now. Applied on load and on save, so neither a hand-edited
    // config nor a preference stored by a later build that does offer `Continue` can leave the page on a layout
    // this one will not show.
    public ProjectsDisplaySettings Normalized() =>
        LayoutMode == ProjectsLayoutMode.Continue && !ContinueLayoutAvailable
            ? this with { LayoutMode = ProjectsLayoutMode.Cards }
            : this;
}
