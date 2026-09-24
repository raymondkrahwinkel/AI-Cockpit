using System.Text.Json;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Configuration;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Infrastructure.Plugins;

// AC-1294: the plugin cache lives in its own top-level file next to `cockpit.json`, one slice per plugin id.
// Its own file because a restore replaces a plugin's whole `cockpit.json` slice at once — cache kept there is
// the source machine's cache afterwards — and because a name not on `BackupContents.Included` is never archived.
public sealed class PluginCacheStore
{
    public const string FileName = "plugin-cache.json";

    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly ILogger<PluginCacheStore>? _logger;
    private readonly Dictionary<string, Dictionary<string, string>> _slices;

    // AC-1391: `logger` optional, like the rest of this folder's install/discovery classes — this is
    // constructed both by DI-less call sites (tests) and by `ForStateRoot` before the container exists.
    public PluginCacheStore(string path, ILogger<PluginCacheStore>? logger = null)
    {
        _path = path;
        _logger = logger;
        _slices = _Read(path, logger);
    }

    public static PluginCacheStore ForStateRoot(ILogger<PluginCacheStore>? logger = null) =>
        new(Path.Combine(CockpitBuild.StateRoot, FileName), logger);

    // The cache a plugin sees: `PluginStorage` does the Get/Set work over this plugin's slice, behind a front
    // that has no `SetSecret` to reach even by casting.
    public IPluginCache CreateFor(string pluginId) =>
        new PluginCache(new PluginStorage(Load(pluginId), values => Save(pluginId, values)));

    public IReadOnlyDictionary<string, string> Load(string pluginId)
    {
        lock (_lock)
        {
            return _slices.TryGetValue(pluginId, out var slice)
                ? new Dictionary<string, string>(slice)
                : new Dictionary<string, string>();
        }
    }

    public void Save(string pluginId, IReadOnlyDictionary<string, string> values)
    {
        lock (_lock)
        {
            _slices[pluginId] = new Dictionary<string, string>(values);

            try
            {
                // Written whole and synchronously under the lock: one small file with no scrubbing or encryption
                // pass to schedule around, and a cache write nobody waits for is a cache write nobody can order.
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(_slices));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(exception, "Could not write the plugin cache ({Path}).", _path);
            }
        }
    }

    // A cache that cannot be read is an empty cache, never a failed start: the plugins rebuild what was in it,
    // and nothing here is worth risking the settings — which live in another file — over.
    private static Dictionary<string, Dictionary<string, string>> _Read(string path, ILogger<PluginCacheStore>? logger)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path))
                ?? [];
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(exception, "The plugin cache ({Path}) is unreadable and is being ignored.", path);

            return [];
        }
    }
}

// The plugin-facing front for one slice. Reuses `PluginStorage` verbatim — same Get/Set semantics, same
// snapshot-before-persist rule — while keeping its `SetSecret` out of reach: see `IPluginCache` for why.
internal sealed class PluginCache(PluginStorage values) : IPluginCache
{
    public T? Get<T>(string key) => values.Get<T>(key);

    public void Set<T>(string key, T value) => values.Set(key, value);
}
