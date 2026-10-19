using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;

namespace Cockpit.Plugin.Depot.Browser;

// `UploadUrl` is the pre-signed PUT target for a binary file (never logged, never stored); null for a document, which is
// already written. `Checksum` is Depot's own, when its answer carried one.
internal sealed record DepotUploadStarted(string? UploadUrl, string? Checksum);

// AC-1574: the upload core of AC-1573, shared by the file browser and the publisher. A text file is a document (`write`);
// anything else gets a ticket for the caller to PUT to. share "none": the default would leave a share behind on a failed PUT.
internal sealed class DepotUploader(DepotMcp mcp)
{
    public async Task<DepotUploadStarted> StartAsync(
        string connectionId, string project, string path, string? text, string contentType, long size, string? baseChecksum,
        CancellationToken cancellationToken)
    {
        if (DepotUploadKinds.IsText(path))
        {
            if (text is null || Encoding.UTF8.GetByteCount(text) > DepotUploadKinds.MaxInlineBytes)
            {
                throw new DepotFailure(DepotBrowserStatus.Error, "A text file can be at most 8 MiB.");
            }

            var arguments = new Dictionary<string, object?> { ["project"] = project, ["path"] = path, ["content"] = text };
            if (baseChecksum is not null)
            {
                arguments["baseChecksum"] = baseChecksum;
            }

            return new DepotUploadStarted(null, _Checksum(await mcp.CallAsync(connectionId, "write", arguments, cancellationToken).ConfigureAwait(false)));
        }

        if (size > DepotUploadKinds.MaxBlobBytes)
        {
            throw new DepotFailure(DepotBrowserStatus.Error, "A file can be at most 100 MiB.");
        }

        using var ticket = DepotMcp.Parse(await mcp.CallAsync(
            connectionId,
            "request_upload",
            new Dictionary<string, object?>
            {
                ["project"] = project,
                ["path"] = path,
                ["contentType"] = contentType.Length > 0 ? contentType : "application/octet-stream",
                ["size"] = size,
                ["share"] = "none",
            },
            cancellationToken).ConfigureAwait(false));
        return DepotMcp.String(ticket.RootElement, "uploadUrl") is { Length: > 0 } url
            ? new DepotUploadStarted(url, null)
            : throw new DepotFailure(DepotBrowserStatus.Error, "Depot did not return an upload URL.");
    }

    // The PUT of a ticket from StartAsync, from the machine the file is on. Answers Depot's checksum of what it stored.
    public static async Task<string?> PutAsync(HttpClient http, string uploadUrl, Stream content, string contentType, CancellationToken cancellationToken)
    {
        try
        {
            using var body = new StreamContent(content);
            body.Headers.ContentType = new MediaTypeHeaderValue(contentType.Length > 0 ? contentType : "application/octet-stream");
            using var response = await http.PutAsync(uploadUrl, body, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? _Checksum(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))
                : throw new DepotFailure(DepotBrowserStatus.Error, $"Depot storage refused the file (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception exception) when (exception is HttpRequestException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new DepotFailure(DepotBrowserStatus.Error, "Could not reach Depot storage, or it took too long.");
        }
    }

    private static string? _Checksum(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return DepotMcp.String(document.RootElement, "checksum");
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
