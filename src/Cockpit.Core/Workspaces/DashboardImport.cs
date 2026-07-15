namespace Cockpit.Core.Workspaces;

/// <summary>
/// What came out of an imported file: the dashboard, the config to store for each of its new instances, and
/// the widget types this cockpit does not have.
/// </summary>
/// <param name="MissingWidgetIds">
/// The widget types that were skipped, once each. Not an error — the dashboard imported, minus these — but the
/// operator has to be told, or a shared dashboard silently arrives with holes in it and looks broken rather
/// than incomplete. Naming the type is what makes it actionable: it is the plugin to go and install.
/// </param>
public sealed record DashboardImport(
    Workspace Workspace,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Config,
    IReadOnlyList<string> MissingWidgetIds)
{
    /// <summary>True when every widget in the file was available — the import is whole.</summary>
    public bool IsComplete => MissingWidgetIds.Count == 0;
}
