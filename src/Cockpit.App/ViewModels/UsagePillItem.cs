namespace Cockpit.App.ViewModels;

// One segment of the session header's usage pill (AC-105): its rendered text (e.g. `ctx 82%`), the theme
// brush key its text takes from its severity (`UsageSeverity`; neutral for a metric with no limit,
// such as the running token/cost total), the hover detail, and whether a divider precedes it — the segments butt
// together inside one rounded pill, so every segment but the first draws a thin separator on its left. Built by
// `SessionPanelViewModel` from the operator's chosen fields, only for the metrics the session has.
public sealed record UsagePillItem(string DisplayText, string SeverityBrushKey, string Tooltip, bool ShowLeadingDivider = false);
