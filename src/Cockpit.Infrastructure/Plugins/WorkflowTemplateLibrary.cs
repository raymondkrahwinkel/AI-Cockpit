using System.Text.Json;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Plugins;

// Workflow templates installed from a store (#69), kept as plain flow files — no assembly to load, nothing to
// consent to. Stored with the store's metadata (name, publisher, source plugins) so the picker can attribute
// origin and refuse to open a template whose steps this build cannot resolve.
internal sealed class WorkflowTemplateLibrary : IWorkflowTemplateLibrary, ISingletonService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _root;

    // The templates live beside the plugins, under the cockpit's own config directory.
    public WorkflowTemplateLibrary()
        : this(Path.Combine(Path.GetDirectoryName(CockpitConfigPath.Default)!, "workflow-templates"))
    {
    }

    // Test seam: a library rooted somewhere a test may write.
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
