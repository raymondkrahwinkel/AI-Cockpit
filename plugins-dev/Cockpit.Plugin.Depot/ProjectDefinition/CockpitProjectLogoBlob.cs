using System.Net.Http.Headers;
using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

// Uploads/downloads `.cockpit/logo.png` (AC-244) — the pre-signed URL comes from Depot's own
// `request_upload`/`request_download` MCP tools via `ICockpitHost.CallMcpToolAsync`, but
// the bytes themselves go over a plain HTTP PUT/GET, per Depot's own contract for those tools.
public static class CockpitProjectLogoBlob
{
    public const string BlobPath = ".cockpit/logo.png";

    private static readonly HttpClient _Http = new();

    public static async Task<CockpitProjectLogoUploadResult> UploadAsync(
        ICockpitHost host, string mcpServerName, string depotProjectSlug, byte[] pngBytes,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var result = await host.CallMcpToolAsync(
            mcpServerName,
            "request_upload",
            new Dictionary<string, object?>
            {
                ["project"] = depotProjectSlug,
                ["path"] = BlobPath,
                ["contentType"] = "image/png",
                ["size"] = pngBytes.Length,
            },
            projectId: null,
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome == PluginMcpToolCallOutcome.AuthorizationRequired)
        {
            return CockpitProjectLogoUploadResult.AuthorizationRequired;
        }

        if (result.Outcome != PluginMcpToolCallOutcome.Success)
        {
            return CockpitProjectLogoUploadResult.Failed(
                result.Error is { Length: > 0 } error ? error : "Depot did not return an upload URL.");
        }

        if (!_TryReadUrl(result.Content ?? string.Empty, "uploadUrl", out var uploadUrl))
        {
            return CockpitProjectLogoUploadResult.Failed("Depot's upload response came back in an unexpected shape.");
        }

        try
        {
            using var content = new ByteArrayContent(pngBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            using var response = await (httpClient ?? _Http).PutAsync(uploadUrl, content, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? CockpitProjectLogoUploadResult.Success
                : CockpitProjectLogoUploadResult.Failed($"Uploading the logo failed with HTTP {(int)response.StatusCode}.");
        }
        catch (HttpRequestException exception)
        {
            return CockpitProjectLogoUploadResult.Failed($"Uploading the logo failed: {exception.Message}");
        }
    }

    public static async Task<CockpitProjectLogoDownloadResult> DownloadAsync(
        ICockpitHost host, string mcpServerName, string depotProjectSlug,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var result = await host.CallMcpToolAsync(
            mcpServerName,
            "request_download",
            new Dictionary<string, object?> { ["project"] = depotProjectSlug, ["path"] = BlobPath },
            projectId: null,
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome == PluginMcpToolCallOutcome.AuthorizationRequired)
        {
            return CockpitProjectLogoDownloadResult.AuthorizationRequired;
        }

        if (result.Outcome != PluginMcpToolCallOutcome.Success)
        {
            return CockpitProjectLogoDownloadResult.Failed(
                result.Error is { Length: > 0 } error ? error : "Depot did not return a download URL.");
        }

        if (!_TryReadUrl(result.Content ?? string.Empty, "downloadUrl", out var downloadUrl))
        {
            return CockpitProjectLogoDownloadResult.Failed("Depot's download response came back in an unexpected shape.");
        }

        try
        {
            var bytes = await (httpClient ?? _Http).GetByteArrayAsync(downloadUrl, cancellationToken).ConfigureAwait(false);
            return CockpitProjectLogoDownloadResult.Success(bytes);
        }
        catch (HttpRequestException exception)
        {
            return CockpitProjectLogoDownloadResult.Failed($"Downloading the logo failed: {exception.Message}");
        }
    }

    // AC-763. Soft-deletes the blob via Depot's own `delete` tool — no pre-signed URL/HTTP leg, unlike
    // Upload/Download, since `delete` is an ordinary MCP call. Idempotent on purpose: a retry after a save that
    // failed partway (or two machines removing the same shared logo close together) must not turn "already gone"
    // into an error — Depot's own not-found wording (the same `"[NotFound]"` prefix `DepotSharedProjectSource.PublishAsync`
    // already reads) is what tells that apart from a real failure.
    public static async Task<CockpitProjectLogoDeleteResult> DeleteAsync(
        ICockpitHost host, string mcpServerName, string depotProjectSlug, CancellationToken cancellationToken = default)
    {
        var result = await host.CallMcpToolAsync(
            mcpServerName,
            "delete",
            new Dictionary<string, object?> { ["project"] = depotProjectSlug, ["path"] = BlobPath, ["kind"] = "artifact" },
            projectId: null,
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome == PluginMcpToolCallOutcome.AuthorizationRequired)
        {
            return CockpitProjectLogoDeleteResult.AuthorizationRequired;
        }

        if (result.Outcome == PluginMcpToolCallOutcome.Success
            || (result.Error is { } error && error.StartsWith("[NotFound]", StringComparison.Ordinal)))
        {
            return CockpitProjectLogoDeleteResult.Success;
        }

        return CockpitProjectLogoDeleteResult.Failed(result.Error is { Length: > 0 } message ? message : "Depot did not confirm the delete.");
    }

    private static bool _TryReadUrl(string json, string propertyName, out string url)
    {
        url = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
            {
                url = value.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return url.Length > 0;
    }
}
