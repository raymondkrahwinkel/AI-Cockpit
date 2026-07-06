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
}
