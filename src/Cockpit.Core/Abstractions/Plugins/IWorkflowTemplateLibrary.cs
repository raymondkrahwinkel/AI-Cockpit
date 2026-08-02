namespace Cockpit.Core.Abstractions.Plugins;

/// <summary>
/// The workflow templates installed from a store (#69). Kept apart from the plugins because a template is not code: a
/// flow as text, written to a file, read back at startup and offered in the editor's picker beside the ones the
/// plugins ship — to the operator they are the same thing, a flow somebody already drew.
/// </summary>
public interface IWorkflowTemplateLibrary
{
    IReadOnlyList<InstalledWorkflowTemplate> Load();

    void Install(InstalledWorkflowTemplate template);

    void Remove(string id);

    bool IsInstalled(string id);
}

// A template as it sits on disk: the flow itself, and what the store said about it — so the picker can say where it
// came from, and can refuse to open one whose steps this build does not have rather than showing a canvas of nodes
// the editor cannot resolve.
//
// `Id`: Stable identity, as the store published it.
// `Name`: What the picker shows.
// `Description`: One line: what the flow does.
// `Json`: The flow, in the workflow editor's own format — the same text a flow is exported to.
// `Author`: Who published it.
// `Version`: The version installed, so an update is a thing that can be seen.
// `Category`: The heading the picker files it under.
// `Requires`: The plugins whose steps this flow uses.
public sealed record InstalledWorkflowTemplate(
    string Id,
    string Name,
    string? Description,
    string Json,
    string? Author = null,
    string? Version = null,
    string? Category = null,
    IReadOnlyList<string>? Requires = null);
