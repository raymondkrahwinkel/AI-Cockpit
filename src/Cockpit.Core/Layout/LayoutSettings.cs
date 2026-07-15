namespace Cockpit.Core.Layout;

/// <summary>
/// User-configurable layout settings, persisted under the <c>layout</c> section of <c>cockpit.json</c>
/// (same store pattern as the transcript-display and session-behaviour settings). Holds whether the
/// cockpit always shows one session at a time instead of the multi-session grid (#24).
/// </summary>
public sealed record LayoutSettings
{
    /// <summary>When true, the cockpit shows only the selected session full-size; you switch sessions from the sidebar. Off = the adaptive grid.</summary>
    public bool SingleSessionLayout { get; init; }

    /// <summary>When true, the multi-session grid stacks panels in a single column (one above the other) instead of tiling them side by side. Off = the adaptive side-by-side grid.</summary>
    public bool StackSessionsVertically { get; init; }

    /// <summary>When true, closing the window hides it to the system tray and keeps the app running instead of quitting (#33). Off by default.</summary>
    public bool MinimizeToTrayOnClose { get; init; }

    /// <summary>
    /// Width in pixels of the left sidebar column (#49), dragged via the <c>GridSplitter</c> between it
    /// and the session content. Clamped to <see cref="MinSidebarWidth"/>/<see cref="MaxSidebarWidth"/> on
    /// load and on save. Defaults to the sidebar's original fixed width.
    /// </summary>
    public double SidebarWidth { get; init; } = DefaultSidebarWidth;

    /// <summary>When true, the left sidebar is collapsed out of view (its width taken by the session content) until expanded again. Off by default; the last-dragged <see cref="SidebarWidth"/> is kept and restored on expand.</summary>
    public bool SidebarCollapsed { get; init; }

    public const double DefaultSidebarWidth = 180;
    public const double MinSidebarWidth = 180;
    public const double MaxSidebarWidth = 480;
}
