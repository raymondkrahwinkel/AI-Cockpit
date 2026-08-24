namespace Cockpit.Core.Workspaces;

// AC-1013: one widget in an exported dashboard. `WidgetId` is the widget *type* id — the instance id is
// deliberately not exported, since an import creates new instances and reusing ids would collide. `Config` is
// the instance's settings with credentials removed (see `DashboardExporter`), stored as raw JSON. (Full text on ticket.)
public sealed record DashboardExportPane(string WidgetId, GridCell Cell, IReadOnlyDictionary<string, string> Config);
