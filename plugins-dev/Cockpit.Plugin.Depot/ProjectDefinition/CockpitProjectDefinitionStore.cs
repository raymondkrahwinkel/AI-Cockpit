using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

// Reads and writes `.cockpit/project.json` in a Depot project through a connection's own MCP server (AC-244), the same `ICockpitHost.CallMcpToolAsync` seam `DepotMemorySource` already uses for `list_projects`.
// AC-244: a baseChecksum mismatch on write surfaces as an ordinary PluginMcpToolCallOutcome.Failed with Depot's own
// error text — there is no separate conflict signal to detect, so WriteAsync does not invent one either.
public static class CockpitProjectDefinitionStore
{
    public const string DefinitionPath = ".cockpit/project.json";

    public static async Task<CockpitProjectDefinitionReadResult> ReadAsync(
        ICockpitHost host, string mcpServerName, string depotProjectSlug, CancellationToken cancellationToken = default)
    {
        var result = await host.CallMcpToolAsync(
            mcpServerName,
            "read",
            new Dictionary<string, object?> { ["project"] = depotProjectSlug, ["path"] = DefinitionPath },
            projectId: null,
            cancellationToken).ConfigureAwait(false);

        return result.Outcome switch
        {
            PluginMcpToolCallOutcome.AuthorizationRequired => CockpitProjectDefinitionReadResult.AuthorizationRequired,
            PluginMcpToolCallOutcome.Success => _ParseReadEnvelope(result.Content ?? string.Empty),
            _ => CockpitProjectDefinitionReadResult.Failed(
                result.Error is { Length: > 0 } error ? error : "Depot did not return a project definition."),
        };
    }

    // `baseChecksum`: From a prior `ReadAsync` — omit only for a project's first write.
    public static async Task<CockpitProjectDefinitionWriteResult> WriteAsync(
        ICockpitHost host, string mcpServerName, string depotProjectSlug, CockpitProjectDefinition definition,
        string? baseChecksum, CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["project"] = depotProjectSlug,
            ["path"] = DefinitionPath,
            ["content"] = CockpitProjectDefinitionJson.Serialize(definition),
        };
        if (baseChecksum is { Length: > 0 })
        {
            arguments["baseChecksum"] = baseChecksum;
        }

        var result = await host.CallMcpToolAsync(mcpServerName, "write", arguments, projectId: null, cancellationToken)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            PluginMcpToolCallOutcome.AuthorizationRequired => CockpitProjectDefinitionWriteResult.AuthorizationRequired,
            PluginMcpToolCallOutcome.Success => _ParseWriteEnvelope(result.Content ?? string.Empty),
            _ => CockpitProjectDefinitionWriteResult.Failed(
                result.Error is { Length: > 0 } error ? error : "Depot did not confirm the write."),
        };
    }

    private static CockpitProjectDefinitionReadResult _ParseReadEnvelope(string json)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<_ReadEnvelope>(json, _SerializerOptions);
            if (envelope?.Content is not { } content || envelope.Checksum is not { Length: > 0 } checksum)
            {
                return CockpitProjectDefinitionReadResult.Failed("Depot's read result came back in an unexpected shape.");
            }

            return CockpitProjectDefinitionJson.TryDeserialize(content, out var definition, out var parseError)
                ? CockpitProjectDefinitionReadResult.Success(definition!, checksum)
                : CockpitProjectDefinitionReadResult.Failed(parseError!);
        }
        catch (JsonException exception)
        {
            return CockpitProjectDefinitionReadResult.Failed($"Couldn't read Depot's response: {exception.Message}");
        }
    }

    private static CockpitProjectDefinitionWriteResult _ParseWriteEnvelope(string json)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<_WriteEnvelope>(json, _SerializerOptions);
            return envelope?.Checksum is { Length: > 0 } checksum
                ? CockpitProjectDefinitionWriteResult.Success(checksum)
                : CockpitProjectDefinitionWriteResult.Failed("Depot's write result came back in an unexpected shape.");
        }
        catch (JsonException exception)
        {
            return CockpitProjectDefinitionWriteResult.Failed($"Couldn't read Depot's response: {exception.Message}");
        }
    }

    private static readonly JsonSerializerOptions _SerializerOptions = new(JsonSerializerDefaults.Web);

    private sealed class _ReadEnvelope
    {
        public string? Content { get; set; }
        public string? Checksum { get; set; }
    }

    private sealed class _WriteEnvelope
    {
        public string? Checksum { get; set; }
    }
}
