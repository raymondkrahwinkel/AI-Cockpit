using System.Net.Http.Headers;
using System.Text.Json;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

// Uploads/downloads `.cockpit/logo.png` (AC-244) — the pre-signed URL comes from Depot's REST API,
// while the bytes themselves go over a plain HTTP PUT/GET without the bearer token.
public static class CockpitProjectLogoBlob
{
    public const string BlobPath = ".cockpit/logo.png";

    private static readonly HttpClient _Http = new();

    public static async Task<CockpitProjectLogoUploadResult> UploadAsync(
        ICockpitHost host, DepotConnectionRegistration connection, string depotProjectAddress, byte[] pngBytes,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var path = _SyncPath(depotProjectAddress, "uploads");
        if (path is null)
        {
            return CockpitProjectLogoUploadResult.Failed(
                $"Depot project '{depotProjectAddress}' has no organization-qualified address.");
        }

        DepotApiResult result;
        try
        {
            using var api = new DepotApi(host, connection, httpClient);
            result = await api.PostJsonAsync(path, new
            {
                files = new[] { new { path = BlobPath, contentType = "image/png", size = pngBytes.Length } },
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return CockpitProjectLogoUploadResult.Failed(exception.Message);
        }

        if (result.Outcome == DepotApiOutcome.NotSignedIn)
        {
            return CockpitProjectLogoUploadResult.AuthorizationRequired;
        }

        if (result.Outcome != DepotApiOutcome.Success)
        {
            return CockpitProjectLogoUploadResult.Failed("Depot did not return an upload URL.");
        }

        if (!_TryReadUrl(result.Content, "uploads", "uploadUrl", out var uploadUrl))
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
        ICockpitHost host, DepotConnectionRegistration connection, string depotProjectAddress,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var path = _SyncPath(depotProjectAddress, "downloads");
        if (path is null)
        {
            return CockpitProjectLogoDownloadResult.Failed(
                $"Depot project '{depotProjectAddress}' has no organization-qualified address.");
        }

        DepotApiResult result;
        try
        {
            using var api = new DepotApi(host, connection, httpClient);
            result = await api.PostJsonAsync(path, new { prefix = BlobPath }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return CockpitProjectLogoDownloadResult.Failed(exception.Message);
        }

        if (result.Outcome == DepotApiOutcome.NotSignedIn)
        {
            return CockpitProjectLogoDownloadResult.AuthorizationRequired;
        }

        if (result.Outcome != DepotApiOutcome.Success)
        {
            return CockpitProjectLogoDownloadResult.Failed("Depot did not return a download URL.");
        }

        if (!_TryReadUrl(result.Content, "files", "downloadUrl", out var downloadUrl))
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

    // AC-763. Soft-deletes through Depot's REST route — no pre-signed URL/HTTP leg, unlike Upload/Download.
    // Idempotent on purpose: REST's 404 means the artifact is already gone.
    public static async Task<CockpitProjectLogoDeleteResult> DeleteAsync(
        ICockpitHost host, DepotConnectionRegistration connection, string depotProjectAddress,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var path = _SyncPath(depotProjectAddress, $"delete?path={Uri.EscapeDataString(BlobPath)}&kind=artifact");
        if (path is null)
        {
            return CockpitProjectLogoDeleteResult.Failed(
                $"Depot project '{depotProjectAddress}' has no organization-qualified address.");
        }

        DepotApiResult result;
        try
        {
            using var api = new DepotApi(host, connection, httpClient);
            result = await api.PostAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return CockpitProjectLogoDeleteResult.Failed(exception.Message);
        }

        if (result.Outcome == DepotApiOutcome.NotSignedIn)
        {
            return CockpitProjectLogoDeleteResult.AuthorizationRequired;
        }

        if (result.Outcome is DepotApiOutcome.Success or DepotApiOutcome.OlderDepot)
        {
            return CockpitProjectLogoDeleteResult.Success;
        }

        return CockpitProjectLogoDeleteResult.Failed("Depot did not confirm the delete.");
    }

    private static string? _SyncPath(string projectAddress, string operation)
    {
        var address = projectAddress.Split('/', 2);
        return address.Length == 2 && address.All(part => !string.IsNullOrWhiteSpace(part))
            ? $"sync/{Uri.EscapeDataString(address[0])}/{Uri.EscapeDataString(address[1])}/{operation}"
            : null;
    }

    private static bool _TryReadUrl(ReadOnlyMemory<byte> json, string collectionName, string propertyName, out string url)
    {
        url = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty(collectionName, out var collection)
                && collection.ValueKind == JsonValueKind.Array
                && collection.GetArrayLength() > 0
                && collection[0].TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.String)
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
