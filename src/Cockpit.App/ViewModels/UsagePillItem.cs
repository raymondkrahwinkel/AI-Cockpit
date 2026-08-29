namespace Cockpit.App.ViewModels;

// AC-105 session-header pill segment: chosen metrics get severity styling, hover detail and a non-first divider.
public sealed record UsagePillItem(string DisplayText, string SeverityBrushKey, string Tooltip, bool ShowLeadingDivider = false);
