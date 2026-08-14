namespace Cockpit.Core.Projects;

// AC-772: three arrangements of the same page — same wording, same actions — which is why this is one preference
// rather than three screens.
public enum ProjectsLayoutMode
{
    // The default: one card per project, wrapped. What the page has always looked like.
    Cards,

    // Wide rows. Roughly twice as many projects on a screen, at the cost of a smaller picture to recognise them by.
    List,

    // Sorted by what was worked on last. See `ProjectsDisplaySettings.ContinueLayoutAvailable`.
    Continue,
}

// AC-772: persisted under the `projects` section of `cockpit.json`, same store pattern as `LayoutSettings`. No
// Options page — the choice is made on the page it changes.
public sealed record ProjectsDisplaySettings
{
    // AC-772 criterion 19: `Continue` leans on `Project.LastOpenedAt`, whose reliability is unverified — so the
    // segment is not offered and a stored choice of it falls back to `Cards`.
    public const bool ContinueLayoutAvailable = false;

    public ProjectsLayoutMode LayoutMode { get; init; } = ProjectsLayoutMode.Cards;

    // Applied on load as well as save, so a hand-edited config or a later build's preference cannot leave the page
    // on a layout this one will not draw.
    public ProjectsDisplaySettings Normalized() =>
        LayoutMode == ProjectsLayoutMode.Continue && !ContinueLayoutAvailable
            ? this with { LayoutMode = ProjectsLayoutMode.Cards }
            : this;
}
