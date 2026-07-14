using System.Text.Json;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Plugins;

/// <summary>
/// The workflow templates installed from a store (#69), kept as files beside the plugins. A template is a flow as
/// text, so installing one is writing a file — there is no assembly to load and nothing to consent to running. The
/// cockpit reads them at startup and offers them in the editor's picker next to the ones the plugins ship, because to
/// the operator they are the same thing: a flow somebody already drew.
/// <para>
/// Each one is stored with what the store said about it (name, description, who published it, which plugins its steps
/// come from), so the picker can say where a template came from and refuse to open one whose steps this build does not
/// have — rather than opening a canvas of nodes the editor cannot resolve.
/// </para>
/// </summary>
internal sealed class WorkflowTemplateLibrary : IWorkflowTemplateLibrary, ISingletonService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _root;

    /// <summary>The templates live beside the plugins, under the cockpit's own config directory.</summary>
    public WorkflowTemplateLibrary()
        : this(Path.Combine(Path.GetDirectoryName(CockpitConfigPath.Default)!, "workflow-templates"))
    {
    }

    /// <summary>Test seam: a library rooted somewhere a test may write.</summary>
    internal WorkflowTemplateLibrary(string root)
    {
        _root = root;
    }

    private string Root => _root;

    public IReadOnlyList<InstalledWorkflowTemplate> Load()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        var templates = new List<InstalledWorkflowTemplate>();
        foreach (var file in Directory.EnumerateFiles(Root, "*.json"))
        {
            // A template that cannot be read costs the operator that template, not the app: a hand-edited or
            // half-written file is skipped, and the rest of the library still opens.
            try
            {
                if (JsonSerializer.Deserialize<InstalledWorkflowTemplate>(File.ReadAllText(file), Options) is { } template)
                {
                    templates.Add(template);
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }

        return templates.OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Install(InstalledWorkflowTemplate template)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(_PathOf(template.Id), JsonSerializer.Serialize(template, Options));
    }

    public void Remove(string id)
    {
        var path = _PathOf(id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public bool IsInstalled(string id) => File.Exists(_PathOf(id));

    // The id is a file name, and a store's id is a string the cockpit did not write: anything that could climb out of
    // the directory is replaced rather than trusted.
    private string _PathOf(string id)
    {
        var safe = string.Concat(id.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-'));

        return Path.Combine(Root, $"{safe}.json");
    }
}
