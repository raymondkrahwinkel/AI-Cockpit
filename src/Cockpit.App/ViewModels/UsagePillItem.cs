namespace Cockpit.App.ViewModels;

/// <summary>
/// One segment of the session header's usage pill (AC-105): its rendered text (e.g. <c>ctx 82%</c>), the theme
/// brush key its text takes from its severity (<see cref="UsageSeverity"/>; neutral for a metric with no limit,
/// such as the running token/cost total), the hover detail, and whether a divider precedes it — the segments butt
/// together inside one rounded pill, so every segment but the first draws a thin separator on its left. Built by
/// <see cref="SessionPanelViewModel"/> from the operator's chosen fields, only for the metrics the session has.
/// </summary>
public sealed record UsagePillItem(string DisplayText, string SeverityBrushKey, string Tooltip, bool ShowLeadingDivider = false);
