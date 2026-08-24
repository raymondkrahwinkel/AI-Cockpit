namespace Cockpit.Core.Workspaces;

// AC-1013: what an import produced — the dashboard, config per new instance, and widget types this cockpit
// lacks. `MissingWidgetIds` (deduped) is not an error: the dashboard imported minus these, but the operator
// must be told or it looks broken rather than incomplete — naming the type makes it actionable. (Full text on ticket.)
public sealed record DashboardImport(
    Workspace Workspace,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Config,
    IReadOnlyList<string> MissingWidgetIds)
{
    // True when every widget in the file was available — the import is whole.
    public bool IsComplete => MissingWidgetIds.Count == 0;
}
