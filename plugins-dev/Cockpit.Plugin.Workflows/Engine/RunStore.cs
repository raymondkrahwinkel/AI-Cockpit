using System.Text.Json;
using System.Text.Json.Serialization;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// Keeps what happened (#69). "It did not work" is not something an operator can act on, so every run is written
/// down — which step got what, what it produced, how long it took — and kept until the next twenty push it out.
/// A run history that grows without bound is a config file that grows without bound.
/// </summary>
internal sealed class RunStore(IPluginStorage storage)
{
    private const string Key = "runs";
    private const int Keep = 20;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public IReadOnlyList<WorkflowRun> Load()
    {
        try
        {
            return storage.Get<string>(Key) is { Length: > 0 } json
                ? JsonSerializer.Deserialize<List<WorkflowRun>>(json, Options) ?? []
                : [];
        }
        catch (JsonException)
        {
            // A history we cannot read costs you the history, not the plugin.
            return [];
        }
    }

    /// <summary>Adds a run and drops the oldest beyond <see cref="Keep"/>.</summary>
    public IReadOnlyList<WorkflowRun> Add(WorkflowRun run)
    {
        var runs = Load().ToList();
        runs.Insert(0, run);

        if (runs.Count > Keep)
        {
            runs = runs.Take(Keep).ToList();
        }

        storage.Set(Key, JsonSerializer.Serialize(runs, Options));
        return runs;
    }

    public IReadOnlyList<WorkflowRun> For(string workflowId) =>
        Load().Where(run => run.WorkflowId == workflowId).ToList();
}
