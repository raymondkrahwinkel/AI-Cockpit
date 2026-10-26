using System.Text.Json;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

// Reads `.cockpit/project.json` inline from Depot's project feed and writes it through the checksum-guarded REST route.
public static class CockpitProjectDefinitionStore
{
    public const string DefinitionPath = ".cockpit/project.json";

    public static async Task<CockpitProjectDefinitionReadResult> ReadAsync(
        ICockpitHost host, DepotConnectionRegistration connection, string depotProjectAddress,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var source = httpClient is null
            ? DepotProjectListingSource.For(host, connection)
            : new DepotProjectListingSource(host, connection, httpClient);
        var result = await source.ListAsync(cancellationToken).ConfigureAwait(false);
        if (result.Outcome == DepotProjectListingOutcome.NotSignedIn)
        {
            return CockpitProjectDefinitionReadResult.AuthorizationRequired;
        }

        if (result.Outcome != DepotProjectListingOutcome.Success || !result.IncludesCockpit)
        {
            return CockpitProjectDefinitionReadResult.Failed(
                result.Error is { Length: > 0 } error ? error : "Depot did not return project definitions through its REST API.");
        }

        var project = result.Projects.SingleOrDefault(candidate =>
            string.Equals(candidate.Key, depotProjectAddress, StringComparison.OrdinalIgnoreCase));
        if (project?.Cockpit is not { } cockpit)
        {
            return CockpitProjectDefinitionReadResult.Failed($"[NotFound] No Depot project '{depotProjectAddress}' was found.");
        }

        return cockpit.State switch
        {
            "ok" when cockpit.Definition is { } definition && cockpit.Checksum is { Length: > 0 } checksum =>
                CockpitProjectDefinitionReadResult.Success(definition, checksum),
            "absent" => CockpitProjectDefinitionReadResult.Failed($"[NotFound] '{DefinitionPath}' does not exist."),
            "tooLarge" => CockpitProjectDefinitionReadResult.Failed($"'{DefinitionPath}' is larger than this Cockpit version accepts."),
            "invalid" => CockpitProjectDefinitionReadResult.Failed($"'{DefinitionPath}' is not valid JSON."),
            _ => CockpitProjectDefinitionReadResult.Failed("Depot's project definition came back in an unexpected shape."),
        };
    }

    // `baseChecksum`: From a prior `ReadAsync`; an empty value sends If-None-Match: * for a project's first write.
    // `callerRole`: The caller's `CockpitProjectRole` on this project, when already known. A current Depot's
    // path-aware `cockpit.canWrite` wins; an older Depot without that field keeps the role-based guard.
    public static async Task<CockpitProjectDefinitionWriteResult> WriteAsync(
        ICockpitHost host, DepotConnectionRegistration connection, string depotProjectAddress,
        CockpitProjectDefinition definition, string? baseChecksum, CockpitProjectRole? callerRole = null,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var listing = await (httpClient is null
                ? DepotProjectListingSource.For(host, connection)
                : new DepotProjectListingSource(host, connection, httpClient))
            .ListAsync(cancellationToken).ConfigureAwait(false);
        var listedProject = listing.Outcome == DepotProjectListingOutcome.Success
            ? listing.Projects.SingleOrDefault(candidate =>
                string.Equals(candidate.Key, depotProjectAddress, StringComparison.OrdinalIgnoreCase))
            : null;
        var listedRole = callerRole ?? CockpitProjectRoles.TryParse(listedProject?.Role);
        if (listedProject?.Cockpit?.CanWrite is false)
        {
            return CockpitProjectDefinitionWriteResult.PermissionDenied(
                "This project's Cockpit definition is read-only for you.");
        }

        if (listedProject?.Cockpit?.CanWrite is null && listedRole is { } fallbackRole && !fallbackRole.CanWrite())
        {
            return CockpitProjectDefinitionWriteResult.PermissionDenied(fallbackRole.WriteDeniedReason());
        }

        // AC-607 decision 3: never forward a secret-shaped, unencrypted extension field. Apply the guard to a
        // copy so WriteAsync never changes the definition the caller still holds.
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

        var address = depotProjectAddress.Split('/', 2);
        if (address.Length != 2 || address.Any(string.IsNullOrWhiteSpace))
        {
            return CockpitProjectDefinitionWriteResult.Failed(
                $"Depot project '{depotProjectAddress}' has no organization-qualified address.");
        }

        DepotApiResult result;
        try
        {
            using var api = new DepotApi(host, connection, httpClient);
            var path = $"cockpit/projects/{Uri.EscapeDataString(address[0])}/{Uri.EscapeDataString(address[1])}/definition";
            result = await api.PutJsonAsync(path, outgoing, baseChecksum, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return CockpitProjectDefinitionWriteResult.Failed(exception.Message);
        }

        return result.Outcome switch
        {
            DepotApiOutcome.NotSignedIn => CockpitProjectDefinitionWriteResult.AuthorizationRequired,
            DepotApiOutcome.Forbidden => CockpitProjectDefinitionWriteResult.PermissionDenied("You do not have permission to write this project definition."),
            DepotApiOutcome.Conflict => CockpitProjectDefinitionWriteResult.Conflict(),
            DepotApiOutcome.TooLarge => CockpitProjectDefinitionWriteResult.PayloadTooLarge("The project definition is larger than this Depot accepts."),
            DepotApiOutcome.Success => _ParseWriteEnvelope(result.Content.Span, allDroppedKeys),
            _ => CockpitProjectDefinitionWriteResult.Failed("Depot did not confirm the write."),
        };
    }

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

    private static CockpitProjectDefinitionWriteResult _ParseWriteEnvelope(
        ReadOnlySpan<byte> json, IReadOnlyList<string> droppedExtensionKeys)
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

    private sealed class _WriteEnvelope
    {
        public string? Checksum { get; set; }
    }
}
