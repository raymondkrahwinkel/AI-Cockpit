using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Discord.Tests;

// An in-memory `IPluginStorage` for tests — stores values directly (no JSON round-trip), enough to drive
// `DiscordChannelSettings` and the shared `AssistantChannelStorage` behind it.
internal sealed class FakePluginStorage : IPluginStorage
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public T? Get<T>(string key) => _values.TryGetValue(key, out var value) && value is T typed ? typed : default;

    public void Set<T>(string key, T value) => _values[key] = value;

    public void SetSecret(string key, string value) => _values[key] = value;

    public string? GetSecret(string key) => Get<string>(key);
}
