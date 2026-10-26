using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Configuration;

// AC-1507: a key a manifest can manage, named by its cockpit.json path: a white-listed setting ("Terminal.FontSize"),
// "Plugins.<id>.Enabled", or a whole profile as "Profiles.<label>". A profile is read as its carried fields only
// (AC-540), so nothing machine-bound or secret ever lands in the Provisioning record.
internal static class ManagedConfigKeys
{
    private const string PluginsPrefix = "Plugins.";

    private const string EnabledSuffix = ".Enabled";

    private const string ProfilesPrefix = "Profiles.";

    private static readonly JsonSerializerOptions Compact = new(CockpitConfigFileAccess.SerializerOptions) { WriteIndented = false };

    // Where a refused write is said; the host sets it at startup.
    public static ILogger Log { get; set; } = NullLogger.Instance;

    // The one guard on every write of cockpit.json: a managed key changed by anyone but the applier is put back.
    public static void Enforce(CockpitConfigFile file)
    {
        foreach (var (key, managed) in file.Provisioning?.Managed ?? [])
        {
            if ((Read(file, key) ?? "null") == managed.Value)
            {
                continue;
            }

            // A value this build cannot read back (a hand-edited record, a changed field type) must not fail every save.
            try
            {
                Write(file, key, managed.Value);
                Log.LogWarning("Refused a change to {Key}: it is managed by {By}.", key, managed.By);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
            {
                Log.LogWarning("Could not put back {Key}, managed by {By}: {Reason}", key, managed.By, exception.Message);
            }
        }
    }

    // Null when the key is not there at all.
    public static string? Read(CockpitConfigFile file, string key)
    {
        if (_Plugin(key) is { } id)
        {
            return file.Plugins.TryGetValue(id, out var entry) ? Json(entry.Enabled) : null;
        }

        if (_Profile(key) is { } label)
        {
            return file.Profiles.FirstOrDefault(profile => profile.Label == label) is { } found ? Carried(found) : null;
        }

        if (Chain(key) is not { } chain)
        {
            return null;
        }

        object? target = file;
        foreach (var property in chain)
        {
            target = target is null ? null : property.GetValue(target);
        }

        return target is null ? null : Json(target);
    }

    // A profile's JSON is overlaid, so its machine-bound fields stay as they are.
    public static void Write(CockpitConfigFile file, string key, string json)
    {
        if (_Plugin(key) is { } id)
        {
            var entry = file.Plugins.GetValueOrDefault(id) ?? new PluginRegistrationEntry();
            entry.Enabled = JsonSerializer.Deserialize<bool>(json);
            file.Plugins[id] = entry;
            return;
        }

        if (_Profile(key) is { } label)
        {
            var index = file.Profiles.FindIndex(profile => profile.Label == label);
            var node = index < 0 ? new JsonObject() : _Node(file.Profiles[index]);
            _Overlay(node, JsonNode.Parse(json)?.AsObject() ?? []);
            var written = node.Deserialize<SessionProfileEntry>(CockpitConfigFileAccess.SerializerOptions) ?? new SessionProfileEntry();
            if (index < 0)
            {
                file.Profiles.Add(written);
            }
            else
            {
                file.Profiles[index] = written;
            }

            return;
        }

        // A key this build cannot name (a hand-edited record, a renamed field) is left alone rather than failing every save.
        if (Chain(key) is { } chain)
        {
            Set(file, chain, JsonSerializer.Deserialize(json, chain[^1].PropertyType, CockpitConfigFileAccess.SerializerOptions));
        }
    }

    // A setting still at the value a fresh section starts with is one nobody chose (§4.2 "l is missing").
    public static bool IsUnset(string key, string? json) =>
        json is null || (Chain(key) is { } chain && json == Json(chain[^1].GetValue(Activator.CreateInstance(chain[^1].DeclaringType ?? typeof(object)))));

    public static string Hash(string json) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    // Read in the shape the profile store saves (its round trip migrates a Claude profile), so a save does not read as a change.
    public static string Carried(SessionProfileEntry profile) =>
        (JsonNode.Parse(SettingsCarrierWhitelist.Extract(new JsonObject { ["Profiles"] = new JsonArray(_Node(SessionProfileEntry.FromDomain(profile.ToDomain()))) }))?["Profiles"]?[0])?.ToJsonString() ?? "{}";

    // Null for a path through a list or a dictionary: those are not one value to set.
    public static PropertyInfo[]? Chain(string path)
    {
        var chain = new List<PropertyInfo>();
        var type = typeof(CockpitConfigFile);
        foreach (var name in path.Split('.'))
        {
            if (type.GetProperty(name) is not { CanWrite: true } property)
            {
                return null;
            }

            chain.Add(property);
            type = property.PropertyType;
        }

        return chain.Take(chain.Count - 1).Any(property => property.PropertyType.IsGenericType) ? null : [.. chain];
    }

    public static void Set(object target, PropertyInfo[] chain, object? value)
    {
        foreach (var property in chain[..^1])
        {
            var section = property.GetValue(target) ?? Activator.CreateInstance(property.PropertyType) ?? throw new InvalidOperationException($"{property.Name} cannot be created.");
            property.SetValue(target, section);
            target = section;
        }

        chain[^1].SetValue(target, value);
    }

    private static string? _Plugin(string key) =>
        key.StartsWith(PluginsPrefix, StringComparison.Ordinal) && key.EndsWith(EnabledSuffix, StringComparison.Ordinal) && key.Length > PluginsPrefix.Length + EnabledSuffix.Length
            ? key[PluginsPrefix.Length..^EnabledSuffix.Length]
            : null;

    private static string? _Profile(string key) => key.StartsWith(ProfilesPrefix, StringComparison.Ordinal) ? key[ProfilesPrefix.Length..] : null;

    public static string Json(object? value) => JsonSerializer.Serialize(value, Compact);

    private static JsonObject _Node(SessionProfileEntry profile) =>
        JsonSerializer.SerializeToNode(profile, CockpitConfigFileAccess.SerializerOptions)?.AsObject() ?? [];

    private static void _Overlay(JsonObject target, JsonObject source)
    {
        foreach (var (name, value) in source)
        {
            if (value is JsonObject inner && target[name] is JsonObject existing)
            {
                _Overlay(existing, inner);
            }
            else
            {
                target[name] = value?.DeepClone();
            }
        }
    }
}
