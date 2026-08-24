using Cockpit.Core.Secrets;

namespace Cockpit.Core.Workspaces;

// AC-1013: turns a dashboard into something to keep/hand over and back; pure, caller owns widget config storage.
// Scrubbing reuses `SecretFields` (also used by the backup scrubber and at-rest protector) rather than a new
// list, since `SecretFields`'s own remarks warn two lists would drift invisibly. (Trimmed: full text on ticket.)
public static class DashboardExporter
{
    // AC-1013: a dashboard as an export, credentials scrubbed from widget configs. `secrets` should carry the
    // plugin-declared keys alongside the name rule where known; `SecretFields.ByName` alone still catches
    // token/apiKey/secret/password/webhook. A non-Dashboard workspace has no widgets and exports empty.
    public static DashboardExport ToExport(
        Workspace workspace,
        Func<string, IReadOnlyDictionary<string, string>> configFor,
        SecretFields secrets)
    {
        var panes = workspace.Panes
            .Where(pane => pane.Kind == PaneKind.Widget && pane.WidgetId is not null)
            .Select(pane => new DashboardExportPane(pane.WidgetId!, pane.Cell, _Scrub(configFor(pane.Id), secrets)))
            .ToList();

        return new DashboardExport(DashboardExport.CurrentFormatVersion, workspace.Name, workspace.Layout.Clamped(), panes);
    }

    // AC-1013: an export as a new dashboard with fresh instance ids (reusing export ids would let two dashboards
    // write over one widget's settings). A missing widget type is skipped and reported rather than refusing the
    // whole file — Raymond's call: one absent widget should cost that widget, not the dashboard. (Full text on ticket.)
    public static DashboardImport FromExport(DashboardExport export, Func<string, bool> isInstalled, string? name = null)
    {
        var workspace = Workspace.Create(
            string.IsNullOrWhiteSpace(name) ? _NameOr(export.Name) : name.Trim(),
            WorkspaceType.Dashboard) with { Layout = export.Layout.Clamped() };

        var config = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        var missing = new List<string>();

        foreach (var pane in export.Panes)
        {
            if (!isInstalled(pane.WidgetId))
            {
                // Reported once per type: a dashboard with four clocks whose plugin is gone is one thing to
                // install, not four things to read.
                if (!missing.Contains(pane.WidgetId))
                {
                    missing.Add(pane.WidgetId);
                }

                continue;
            }

            var instance = new WorkspacePane(Guid.NewGuid().ToString("n"), PaneKind.Widget)
            {
                WidgetId = pane.WidgetId,
                Cell = pane.Cell,
            };

            workspace = workspace.WithPane(instance);
            config[instance.Id] = pane.Config;
        }

        return new DashboardImport(workspace, config, missing);
    }

    // Whether this build can read the file. A newer format is refused rather than half-read: a dashboard that
    // silently arrives missing whatever the reader did not understand is worse than one that does not arrive.
    public static bool CanRead(DashboardExport export) => export.FormatVersion <= DashboardExport.CurrentFormatVersion;

    private static IReadOnlyDictionary<string, string> _Scrub(IReadOnlyDictionary<string, string> config, SecretFields secrets) =>
        config.Where(entry => !secrets.IsSecret(entry.Key)).ToDictionary(entry => entry.Key, entry => entry.Value);

    private static string _NameOr(string name) => string.IsNullOrWhiteSpace(name) ? "Dashboard" : name.Trim();
}
