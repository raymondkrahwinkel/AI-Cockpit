namespace Cockpit.Core.Workspaces;

/// <summary>
/// One widget in an exported dashboard: which type it is, where it sat, and the configuration that made it
/// show what it showed.
/// </summary>
/// <param name="WidgetId">
/// The widget <em>type</em> id (e.g. "system-monitor.usage"). The instance id is deliberately not exported: an
/// imported dashboard is a new dashboard with new instances, and reusing the ids would have two dashboards
/// writing to one widget's config.
/// </param>
/// <param name="Config">
/// The instance's own settings, with credentials removed (see <see cref="DashboardExporter"/>). Values are the
/// raw JSON the widget stored, since the host does not know a plugin's config shape and must not try to.
/// </param>
public sealed record DashboardExportPane(string WidgetId, GridCell Cell, IReadOnlyDictionary<string, string> Config);
