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

// A template as it sits on disk: the flow itself, plus what the store said about it, so the picker can say
// where it came from and refuse to open one whose steps this build lacks. Id/Name/Description/Category are as
// the store published it; Json is the editor's export format; Requires lists the plugins the flow needs.
public sealed record InstalledWorkflowTemplate(
    string Id,
    string Name,
    string? Description,
    string Json,
    string? Author = null,
    string? Version = null,
    string? Category = null,
    IReadOnlyList<string>? Requires = null);
