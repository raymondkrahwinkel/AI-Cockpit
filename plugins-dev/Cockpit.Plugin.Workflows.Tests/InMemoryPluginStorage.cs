using System.Text.Json;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Workflows.Tests;

// A plugin storage fake shared by this project's tests, lifted out of `WorkflowMcpToolsTests`.
internal sealed class InMemoryPluginStorage : IPluginStorage
{
    private readonly Dictionary<string, string> _values = [];

    public T? Get<T>(string key) => _values.TryGetValue(key, out var value) ? JsonSerializer.Deserialize<T>(value) : default;

    public void Set<T>(string key, T value) => _values[key] = JsonSerializer.Serialize(value);
}
