using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

// Reads and writes `.cockpit/project.json` in a Depot project through a connection's own MCP server (AC-244), the same `ICockpitHost.CallMcpToolAsync` seam `DepotMemorySource` already uses for `list_projects`.
// AC-244: a baseChecksum mismatch on write surfaces as an ordinary PluginMcpToolCallOutcome.Failed with Depot's own
// error text — Depot's MCP layer carries no separate, typed conflict signal (its tool wrapper turns any handler
// failure, conflict or otherwise, into the same shape: an error with only a message). AC-247 classifies that text
// after the fact instead (see CockpitProjectDefinitionWriteResult.Failed) — WriteAsync itself stays a thin,
// unopinionated relay of whatever Depot said.
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
    // `callerRole`: The caller's `CockpitProjectRole` on this project, when already known (e.g. from a prior
    // `list_projects` row) — a role below Editor short-circuits here with
    // `CockpitProjectDefinitionWriteResult.PermissionDenied` and never calls Depot at all. Omit (the
    // default) to skip this local check and rely solely on Depot's own enforcement, which always applies
    // regardless — this parameter only saves the round trip and lets a caller name the reason before it dims a
    // field, it grants nothing Depot itself would not already refuse.
    public static async Task<CockpitProjectDefinitionWriteResult> WriteAsync(
        ICockpitHost host, string mcpServerName, string depotProjectSlug, CockpitProjectDefinition definition,
        string? baseChecksum, CockpitProjectRole? callerRole = null, CancellationToken cancellationToken = default)
    {
        if (callerRole is { } role && !role.CanWrite())
        {
            return CockpitProjectDefinitionWriteResult.PermissionDenied(role.WriteDeniedReason());
        }

        // AC-607 decision 3: never forward an ExtensionData field a newer build wrote if it looks secret-shaped
        // and is not already encrypted — checked both at the definition's own top level and inside each
        // SensitiveFields row's own ExtensionData (finding 4). Applied to a copy, never definition itself —
        // WriteAsync must not change what the caller still holds a reference to.
        var (kept, droppedKeys) = CockpitProjectDefinitionExtensionDataGuard.Apply(definition.ExtensionData);
        var (sensitiveFieldsKept, sensitiveFieldsDroppedKeys) =
            CockpitProjectDefinitionExtensionDataGuard.ApplyToSensitiveFields(definition.SensitiveFields);
        var allDroppedKeys = sensitiveFieldsDroppedKeys.Count == 0 ? droppedKeys : [.. droppedKeys, .. sensitiveFieldsDroppedKeys];
        var outgoing = allDroppedKeys.Count == 0
            ? definition
            : _WithGuardedData(
                definition,
                droppedKeys.Count == 0 ? definition.ExtensionData : kept,
                sensitiveFieldsDroppedKeys.Count == 0 ? definition.SensitiveFields : sensitiveFieldsKept);

        var arguments = new Dictionary<string, object?>
        {
            ["project"] = depotProjectSlug,
            ["path"] = DefinitionPath,
            ["content"] = CockpitProjectDefinitionJson.Serialize(outgoing),
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
            PluginMcpToolCallOutcome.Success => _ParseWriteEnvelope(result.Content ?? string.Empty, allDroppedKeys),
            _ => CockpitProjectDefinitionWriteResult.Failed(
                result.Error is { Length: > 0 } error ? error : "Depot did not confirm the write."),
        };
    }

    // A shallow copy of `definition` with `extensionData` and `sensitiveFields` substituted — every other field
    // carried through unchanged, so the guard's refusal never reaches the caller's own object.
    private static CockpitProjectDefinition _WithGuardedData(
        CockpitProjectDefinition definition, Dictionary<string, JsonElement>? extensionData,
        List<CockpitProjectSensitiveFieldEntry>? sensitiveFields) => new()
    {
        SchemaVersion = definition.SchemaVersion,
        Name = definition.Name,
        Description = definition.Description,
        GitUrl = definition.GitUrl,
        BehaviorPrompt = definition.BehaviorPrompt,
        IsolateInWorktreeByDefault = definition.IsolateInWorktreeByDefault,
        McpOverlay = definition.McpOverlay,
        Resources = definition.Resources,
        Logo = definition.Logo,
        SensitiveFields = sensitiveFields,
        PasswordEnvelope = definition.PasswordEnvelope,
        ExtensionData = extensionData,
    };

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

    private static CockpitProjectDefinitionWriteResult _ParseWriteEnvelope(string json, IReadOnlyList<string> droppedExtensionKeys)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<_WriteEnvelope>(json, _SerializerOptions);
            return envelope?.Checksum is { Length: > 0 } checksum
                ? CockpitProjectDefinitionWriteResult.Success(checksum, droppedExtensionKeys.Count == 0 ? null : droppedExtensionKeys)
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
