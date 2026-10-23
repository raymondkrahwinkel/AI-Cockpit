using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;

namespace Cockpit.Plugin.Depot.Browser;

// `UploadUrl` is the pre-signed PUT target for a binary file (never logged, never stored); null for a document, which is
// already written. `Checksum` is Depot's own, when its answer carried one.
internal sealed record DepotUploadStarted(string? UploadUrl, string? Checksum);

// AC-1574/AC-1637: the upload core shared by the file browser and publisher. Sync writes return an outcome per file;
// binary files get a ticket for the caller to PUT to. share "none" avoids a dangling share when that PUT fails.
internal sealed class DepotUploader
{
    public async Task<DepotUploadStarted> StartAsync(
        DepotApi api, string connectionName, string syncRoot, string path, string? text, string contentType, long size, string? baseChecksum,
        CancellationToken cancellationToken)
    {
        if (DepotUploadKinds.IsText(path))
        {
            if (text is null || Encoding.UTF8.GetByteCount(text) > DepotUploadKinds.MaxInlineBytes)
            {
                throw new DepotFailure(DepotBrowserStatus.Error, "A text file can be at most 8 MiB.");
            }

            using var response = await _PostAsync(
                api, connectionName, $"{syncRoot}/write", new[] { new { path, content = text, baseChecksum } }, cancellationToken)
                .ConfigureAwait(false);
            var result = _Array(response.RootElement, "results").FirstOrDefault(entry => DepotMcp.String(entry, "path") == path);
            return DepotMcp.String(result, "status") switch
            {
                "written" => new DepotUploadStarted(null, DepotMcp.String(result, "checksum")),
                "conflict" => throw new DepotFailure(DepotBrowserStatus.Error, $"[Conflict] {DepotMcp.String(result, "message") ?? "The file changed in Depot."}"),
                _ => throw new DepotFailure(DepotBrowserStatus.Error, DepotMcp.String(result, "message") ?? "Depot did not write the file."),
            };
        }

        if (size > DepotUploadKinds.MaxBlobBytes)
        {
            throw new DepotFailure(DepotBrowserStatus.Error, "A file can be at most 100 MiB.");
        }

        using var ticket = await _PostAsync(
            api,
            connectionName,
            $"{syncRoot}/uploads",
            new
            {
                files = new[]
                {
                    new { path, contentType = contentType.Length > 0 ? contentType : "application/octet-stream", size },
                },
                share = "none",
            },
            cancellationToken).ConfigureAwait(false);
        var upload = _Array(ticket.RootElement, "uploads").FirstOrDefault(entry => DepotMcp.String(entry, "path") == path);
        return DepotMcp.String(upload, "uploadUrl") is { Length: > 0 } url
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

    private static async Task<JsonDocument> _PostAsync<T>(
        DepotApi api, string connectionName, string path, T value, CancellationToken cancellationToken)
    {
        DepotApiResult result;
        try
        {
            result = await api.PostJsonAsync(path, value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new DepotFailure(DepotBrowserStatus.Error, "Could not reach Depot, or it took too long.");
        }

        if (result.Outcome == DepotApiOutcome.NotSignedIn)
        {
            throw new DepotFailure(DepotBrowserStatus.SignIn, $"Sign in to {connectionName}");
        }

        if (result.Outcome != DepotApiOutcome.Success)
        {
            throw new DepotFailure(DepotBrowserStatus.Error, "Depot did not accept the file operation.");
        }

        try
        {
            return JsonDocument.Parse(result.Content);
        }
        catch (JsonException)
        {
            throw new DepotFailure(DepotBrowserStatus.Error, "Depot's answer came back in an unexpected shape.");
        }
    }

    private static IEnumerable<JsonElement> _Array(JsonElement root, string name) =>
        root.TryGetProperty(name, out var entries) && entries.ValueKind == JsonValueKind.Array ? entries.EnumerateArray() : [];
}
