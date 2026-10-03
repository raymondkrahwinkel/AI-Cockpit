using System.Text.Json;
using System.Text.Json.Serialization;
using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Workflows.Engine;

// Keeps what happened (#69). "It did not work" is not something an operator can act on, so every run is written
// down — which step got what, what it produced, how long it took — and kept until the next twenty push it out.
// A run history that grows without bound is a config file that grows without bound.
internal sealed class RunStore(IPluginCache storage, Action<WorkflowRun>? recorded = null)
{
    private const string Key = "runs";
    private const int Keep = 20;
    private readonly Lock _gate = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static void Migrate(IPluginStorage legacyStorage, IPluginCache cache)
    {
        if (cache.Get<string>(Key) is null && legacyStorage.Get<string>(Key) is { } runs)
        {
            cache.Set(Key, runs);
        }

        legacyStorage.Remove(Key);
    }

    public IReadOnlyList<WorkflowRun> Load()
    {
        lock (_gate)
        {
            return _Load();
        }
    }

    private IReadOnlyList<WorkflowRun> _Load()
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

    // Keeps the newest twenty plus the newest run of every other workflow.
    public IReadOnlyList<WorkflowRun> Add(WorkflowRun run)
    {
        List<WorkflowRun> kept;
        lock (_gate)
        {
            var runs = _Load().ToList();
            runs.Insert(0, run);

            var latest = runs
                .GroupBy(candidate => candidate.WorkflowId, StringComparer.Ordinal)
                .Select(group => group.First().Id)
                .ToHashSet(StringComparer.Ordinal);
            kept = runs.Where((candidate, index) => index < Keep || latest.Contains(candidate.Id)).ToList();

            storage.Set(Key, JsonSerializer.Serialize(kept, Options));
        }

        // Whoever ran it: this is how the UI part's run panel follows runs over the channel (AC-1399).
        recorded?.Invoke(run);
        return kept;
    }

    public IReadOnlyList<WorkflowRun> For(string workflowId) =>
        Load().Where(run => run.WorkflowId == workflowId).ToList();
}
