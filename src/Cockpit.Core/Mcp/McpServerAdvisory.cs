using System.Collections.Concurrent;

namespace Cockpit.Core.Mcp;

public enum McpAdvisoryLevel
{
    Ok,
    Warning,
    Critical,
}

// AC-2219: one line a plugin has for an MCP server it contributed (Link: where to read more); the host only shows it.
public sealed record McpServerAdvisory(McpAdvisoryLevel Level, string Text, string? Link = null);

// The advisories by server name. Ok and null both mean nothing to say, so neither is kept.
public sealed class McpServerAdvisories
{
    // ponytail: one process-wide instance, so row view models read it without every dialog being handed a service.
    public static McpServerAdvisories Shared { get; } = new();

    private readonly ConcurrentDictionary<string, McpServerAdvisory> _byName = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? Changed;

    public McpServerAdvisory? Get(string name) => _byName.GetValueOrDefault(name);

    public void Set(string name, McpServerAdvisory? advisory)
    {
        if (advisory is { Level: not McpAdvisoryLevel.Ok })
        {
            _byName[name] = advisory;
        }
        else if (!_byName.TryRemove(name, out _))
        {
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<KeyValuePair<string, McpServerAdvisory>> All => [.. _byName.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)];
}
