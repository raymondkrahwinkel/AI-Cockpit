using System.Collections.Concurrent;
using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpencodeProvider;

internal sealed class AcpImageFacts
{
    private readonly ConcurrentDictionary<string, PluginModelIntelligence> _facts = new();

    public void Record(string config, JsonElement? capabilities)
    {
        if (capabilities is { ValueKind: JsonValueKind.Object } agent
            && agent.TryGetProperty("promptCapabilities", out var prompt)
            && prompt.ValueKind == JsonValueKind.Object
            && prompt.TryGetProperty("image", out var image)
            && image.ValueKind == JsonValueKind.False)
        {
            _facts[config] = new PluginModelIntelligence
            {
                SupportsImageInput = false,
                ImageInputSource = new PluginFactSource("ACP initialize promptCapabilities.image", DateTimeOffset.UtcNow, TimeSpan.FromHours(1)),
            };
        }
        else
        {
            _facts.TryRemove(config, out _);
        }
    }

    public PluginModelIntelligence Read(string config) =>
        _facts.TryGetValue(config, out var fact) ? fact : new PluginModelIntelligence();
}
