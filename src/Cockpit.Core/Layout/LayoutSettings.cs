namespace Cockpit.Core.Layout;

// User-configurable layout settings, persisted under the `layout` section of `cockpit.json`
// (same store pattern as the transcript-display and session-behaviour settings). Holds whether the
// cockpit always shows one session at a time instead of the multi-session grid (#24).
public sealed record LayoutSettings
{
    // When true, the cockpit shows only the selected session full-size; you switch sessions from the sidebar. Off = the adaptive grid.
    public bool SingleSessionLayout { get; init; }

    // When true, the multi-session grid stacks panels in a single column (one above the other) instead of tiling them side by side. Off = the adaptive side-by-side grid.
    public bool StackSessionsVertically { get; init; }

    // When true, closing the window hides it to the system tray and keeps the app running instead of quitting (#33). Off by default.
    public bool MinimizeToTrayOnClose { get; init; }

    // Width in pixels of the left sidebar column (#49), dragged via the `GridSplitter` between it
    // and the session content. Clamped to `MinSidebarWidth`/`MaxSidebarWidth` on
    // load and on save. Defaults to the sidebar's original fixed width.
    public double SidebarWidth { get; init; } = DefaultSidebarWidth;

    // When true, the left sidebar is collapsed out of view (its width taken by the session content) until expanded again. Off by default; the last-dragged `SidebarWidth` is kept and restored on expand.
    public bool SidebarCollapsed { get; init; }

    // Weight of the miniature rail against the focus pane's fixed 1.0 (AC-443), dragged via
    // `FocusRailPanel`'s divider. Clamped to `MinFocusRailWeight`/`MaxFocusRailWeight` on load and save,
    // same as `SidebarWidth`.
    public double FocusRailWeight { get; init; } = DefaultFocusRailWeight;

    public const double DefaultSidebarWidth = 180;
    public const double MinSidebarWidth = 180;
    public const double MaxSidebarWidth = 480;

    public const double DefaultFocusRailWeight = 0.3;
    public const double MinFocusRailWeight = 0.15;
    public const double MaxFocusRailWeight = 1.2;
}
