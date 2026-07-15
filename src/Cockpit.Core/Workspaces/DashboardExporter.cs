using Cockpit.Core.Secrets;

namespace Cockpit.Core.Workspaces;

/// <summary>
/// Turns a dashboard into something you can keep or hand over, and back again. Pure: the caller reads and
/// writes the widget config, this decides what travels.
/// </summary>
/// <remarks>
/// The whole point of the split is the scrubbing. A dashboard you "just share" must not carry an API key, and
/// the rule for what counts as one already exists — <see cref="SecretFields"/>, which the backup scrubber and
/// the at-rest protector both use. This is its third user rather than its second definition: the class's own
/// remarks say two lists would drift, and that the drift is invisible.
/// </remarks>
public static class DashboardExporter
{
    /// <summary>
    /// A dashboard as an export, with every credential dropped from the widget configs.
    /// </summary>
    /// <param name="workspace">The dashboard. A workspace of another type has no widgets and exports as empty.</param>
    /// <param name="configFor">This instance's stored config, by pane id — the caller owns the storage.</param>
    /// <param name="secrets">
    /// What counts as a credential. Pass the plugin-declared keys alongside the name rule where they are known;
    /// <see cref="SecretFields.ByName"/> alone still catches token/apiKey/secret/password/webhook.
    /// </param>
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

    /// <summary>
    /// An export as a new dashboard, plus the config to write for each new instance. Everything gets fresh ids:
    /// an imported dashboard is a new dashboard, and reusing the exporter's instance ids would have two
    /// dashboards writing over one widget's settings.
    /// </summary>
    /// <returns>The workspace, and the config to store per pane id.</returns>
    public static (Workspace Workspace, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Config) FromExport(
        DashboardExport export, string? name = null)
    {
        var workspace = Workspace.Create(
            string.IsNullOrWhiteSpace(name) ? _NameOr(export.Name) : name.Trim(),
            WorkspaceType.Dashboard) with { Layout = export.Layout.Clamped() };

        var config = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        foreach (var pane in export.Panes)
        {
            var instance = new WorkspacePane(Guid.NewGuid().ToString("n"), PaneKind.Widget)
            {
                WidgetId = pane.WidgetId,
                Cell = pane.Cell,
            };

            workspace = workspace.WithPane(instance);
            config[instance.Id] = pane.Config;
        }

        return (workspace, config);
    }

    /// <summary>
    /// Whether this build can read the file. A newer format is refused rather than half-read: a dashboard that
    /// silently arrives missing whatever the reader did not understand is worse than one that does not arrive.
    /// </summary>
    public static bool CanRead(DashboardExport export) => export.FormatVersion <= DashboardExport.CurrentFormatVersion;

    private static IReadOnlyDictionary<string, string> _Scrub(IReadOnlyDictionary<string, string> config, SecretFields secrets) =>
        config.Where(entry => !secrets.IsSecret(entry.Key)).ToDictionary(entry => entry.Key, entry => entry.Value);

    private static string _NameOr(string name) => string.IsNullOrWhiteSpace(name) ? "Dashboard" : name.Trim();
}
