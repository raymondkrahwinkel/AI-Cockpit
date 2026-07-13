using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cockpit.Plugin.Workflows.Model;

/// <summary>
/// A workflow as text. Kept as its own thing (rather than serialising the view models) because a flow has to
/// survive being written to disk, read back, exported to a file and put in git — and because the day a node type
/// gains a setting, an old flow must still open.
/// </summary>
public static class WorkflowJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Write(Workflow workflow) => JsonSerializer.Serialize(workflow, Options);

    public static string WriteAll(IReadOnlyList<Workflow> workflows) => JsonSerializer.Serialize(workflows, Options);

    /// <summary>Reads a workflow back. Returns null on anything that is not one, rather than throwing: a hand-edited or half-written file should cost you that flow, not the plugin.</summary>
    public static Workflow? Read(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Workflow>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IReadOnlyList<Workflow> ReadAll(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<Workflow>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
