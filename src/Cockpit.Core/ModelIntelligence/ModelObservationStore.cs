using System.Text.Json;

namespace Cockpit.Core.ModelIntelligence;

// AC-1812: observations as JSON lines under an injected root; Read drops what is past retention, so the file cannot grow without bound.
public sealed class ModelObservationStore(string root, TimeSpan? retention = null, TimeProvider? clock = null) : IModelObservationStore
{
    private readonly string _path = Path.Combine(root, "model-intelligence", "observations.jsonl");
    private readonly TimeSpan _retention = retention ?? ModelObservationDefaults.Retention;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<(string, string?, string?, string?), double> _lastQuota = [];

    public void Record(ModelObservation observation)
    {
        lock (_gate)
        {
            // A quota reading is stored only when its value changes; after a restart the first one per key is written again.
            if (observation.Kind == ModelObservationKind.QuotaReading)
            {
                var key = (observation.Provider, observation.Profile, observation.Model, observation.Window);
                if (_lastQuota.TryGetValue(key, out var last) && last == observation.Value)
                {
                    return;
                }

                _lastQuota[key] = observation.Value;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, JsonSerializer.Serialize(observation) + "\n");
            }
            catch (IOException)
            {
                // Learning is best effort; a full disk must not break the provider that reported.
            }
        }
    }

    public IReadOnlyList<ModelObservation> Read()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            try
            {
                var lines = File.ReadAllLines(_path);
                var all = lines.Select(_Parse).Where(observation => observation is not null).Cast<ModelObservation>().ToArray();
                var cutoff = _clock.GetUtcNow() - _retention;
                var kept = all.Where(observation => observation.At >= cutoff).ToArray();
                if (kept.Length < lines.Length)
                {
                    File.WriteAllLines(_path, kept.Select(observation => JsonSerializer.Serialize(observation)));
                }

                return kept;
            }
            catch (IOException)
            {
                return [];
            }
        }
    }

    private static ModelObservation? _Parse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<ModelObservation>(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
