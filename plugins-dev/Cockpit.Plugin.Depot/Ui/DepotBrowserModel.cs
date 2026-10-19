using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Depot.UI;

// What an artifact shows inline: a picture or text. Both null is a type that is not previewed.
internal sealed record DepotPreview(Bitmap? Image, string? Text);

// AC-1568: the file browser's behaviour for one Depot connection, apart from the controls that show it. Every Depot
// call goes to the backend part over the channel; the only network this part does itself is the pre-signed URL.
internal sealed class DepotBrowserModel(ICockpitUiHost host, DepotConnectionRegistration connection, HttpClient http)
{
    private const long MaxImageBytes = 8 * 1024 * 1024;
    private const long MaxTextBytes = 1024 * 1024;

    public DepotConnectionRegistration Connection => connection;

    public Task<DepotBrowserAnswer<IReadOnlyList<DepotBrowserProject>>> ProjectsAsync(CancellationToken cancellationToken = default) =>
        _InvokeAsync<IReadOnlyList<DepotBrowserProject>>(DepotBrowserChannel.Projects, new(connection.Id), cancellationToken);

    public Task<DepotBrowserAnswer<DepotBrowserTree>> TreeAsync(string project, CancellationToken cancellationToken = default) =>
        _InvokeAsync<DepotBrowserTree>(DepotBrowserChannel.Tree, new(connection.Id, project), cancellationToken);

    public async Task<DepotBrowserAnswer<Control>> RenderDocumentAsync(string project, DepotBrowserNode node, CancellationToken cancellationToken = default)
    {
        var read = await _InvokeAsync<string>(DepotBrowserChannel.Read, new(connection.Id, project, node.Path), cancellationToken).ConfigureAwait(true);
        return read.Status == DepotBrowserStatus.Ok
            ? DepotBrowserAnswer<Control>.Success(host.CreateMarkdownView(read.Data ?? string.Empty))
            : DepotBrowserAnswer<Control>.Failure(read.Status, read.Message);
    }

    public static bool CanPreview(DepotBrowserNode node) =>
        node.ContentType is { } type
        && ((type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && node.Size <= MaxImageBytes && !type.Contains("svg", StringComparison.OrdinalIgnoreCase))
            || ((type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || type.Contains("json", StringComparison.OrdinalIgnoreCase)) && node.Size <= MaxTextBytes));

    public async Task<DepotBrowserAnswer<DepotPreview>> PreviewAsync(string project, DepotBrowserNode node, CancellationToken cancellationToken = default)
    {
        var fetched = await _FetchAsync(project, node.Path, cancellationToken).ConfigureAwait(true);
        if (fetched.Data is not { } response)
        {
            return DepotBrowserAnswer<DepotPreview>.Failure(fetched.Status, fetched.Message);
        }

        using (response)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(true);
            if (node.ContentType!.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = new MemoryStream(bytes);
                return DepotBrowserAnswer<DepotPreview>.Success(new DepotPreview(new Bitmap(stream), null));
            }

            return DepotBrowserAnswer<DepotPreview>.Success(new DepotPreview(null, Encoding.UTF8.GetString(bytes)));
        }
    }

    // A type with no inline view goes to whatever the system opens that URL with; `launch` is the TopLevel launcher.
    public async Task<DepotBrowserAnswer<bool>> OpenAsync(string project, DepotBrowserNode node, Func<Uri, Task> launch, CancellationToken cancellationToken = default)
    {
        var url = await _InvokeAsync<string>(DepotBrowserChannel.DownloadUrl, new(connection.Id, project, node.Path), cancellationToken).ConfigureAwait(true);
        if (url.Data is null || !Uri.TryCreate(url.Data, UriKind.Absolute, out var uri))
        {
            return DepotBrowserAnswer<bool>.Failure(url.Status, url.Message ?? "Depot did not return a download URL.");
        }

        await launch(uri).ConfigureAwait(true);
        return DepotBrowserAnswer<bool>.Success(true);
    }

    // `openDestination` is the save picker: given the suggested name it returns where to write, or null when the
    // operator cancelled. The name is the last path component, sanitised, never the raw path.
    public async Task<DepotBrowserAnswer<bool>> DownloadAsync(
        string project, DepotBrowserNode node, Func<string, Task<Stream?>> openDestination, CancellationToken cancellationToken = default)
    {
        var name = DepotDownload.SafeFileName(node.Path);
        if (node.Kind == DepotBrowserKind.Document)
        {
            var read = await _InvokeAsync<string>(DepotBrowserChannel.Read, new(connection.Id, project, node.Path), cancellationToken).ConfigureAwait(true);
            if (read.Status != DepotBrowserStatus.Ok)
            {
                return DepotBrowserAnswer<bool>.Failure(read.Status, read.Message);
            }

            await using var file = await openDestination(name).ConfigureAwait(true);
            if (file is null)
            {
                return DepotBrowserAnswer<bool>.Success(false);
            }

            await file.WriteAsync(Encoding.UTF8.GetBytes(read.Data ?? string.Empty), cancellationToken).ConfigureAwait(true);
            return DepotBrowserAnswer<bool>.Success(true);
        }

        var fetched = await _FetchAsync(project, node.Path, cancellationToken).ConfigureAwait(true);
        if (fetched.Data is not { } response)
        {
            return DepotBrowserAnswer<bool>.Failure(fetched.Status, fetched.Message);
        }

        using (response)
        {
            await using var destination = await openDestination(name).ConfigureAwait(true);
            if (destination is null)
            {
                return DepotBrowserAnswer<bool>.Success(false);
            }

            await DepotDownload.CopyAsync(response, destination, cancellationToken).ConfigureAwait(true);
            return DepotBrowserAnswer<bool>.Success(true);
        }
    }

    // Public goes live for anyone with the URL, so it is asked first; a new link also lands on the clipboard.
    // Null when the operator declined.
    public async Task<DepotBrowserAnswer<DepotBrowserShare>?> ShareAsync(
        string project, DepotBrowserNode node, bool isPublic, bool copyLink, CancellationToken cancellationToken = default)
    {
        if (isPublic && !await host.ConfirmAsync(
                "Make this link public?",
                $"Anyone with the URL can open {node.Path}, no account needed, until you stop sharing.",
                "Make public").ConfigureAwait(true))
        {
            return null;
        }

        var answer = await _InvokeAsync<DepotBrowserShare>(
            DepotBrowserChannel.Share, new(connection.Id, project, node.Path, isPublic ? "public" : "members"), cancellationToken).ConfigureAwait(true);
        if (copyLink && answer.Data is { } share)
        {
            await host.SetClipboardTextAsync(share.Url).ConfigureAwait(true);
        }

        return answer;
    }

    // Revoking cannot be undone: sharing again makes a new URL. Null when the operator declined.
    public async Task<DepotBrowserAnswer<bool>?> UnshareAsync(string project, DepotBrowserNode node, CancellationToken cancellationToken = default)
    {
        if (node.ShareId is null
            || !await host.ConfirmAsync(
                "Stop sharing?",
                $"The link to {node.Path} stops working for everyone. Sharing it again makes a new URL.",
                "Stop sharing").ConfigureAwait(true))
        {
            return null;
        }

        return await _InvokeAsync<bool>(DepotBrowserChannel.Unshare, new(connection.Id, project, node.Path, ShareId: node.ShareId), cancellationToken).ConfigureAwait(true);
    }

    // One file into `folder` ("" for the root). A path that already exists is replaced only after a yes; null when the
    // operator declined. A text file travels over the channel as a document, anything else goes by ticket and a PUT.
    public async Task<DepotBrowserAnswer<bool>?> UploadAsync(
        string project, string folder, string fileName, Stream content, bool exists, CancellationToken cancellationToken = default)
    {
        var name = DepotDownload.SafeFileName(fileName);
        var path = folder.Length == 0 ? name : $"{folder}/{name}";
        if (exists && !await host.ConfirmAsync("Replace this file?", $"{path} already exists in this project.", "Replace").ConfigureAwait(true))
        {
            return null;
        }

        if (!content.CanSeek)
        {
            var buffered = new MemoryStream();
            await content.CopyToAsync(buffered, cancellationToken).ConfigureAwait(true);
            buffered.Position = 0;
            content = buffered;
        }

        var request = new DepotBrowserRequest(connection.Id, project, path, Replace: exists);
        if (DepotUploadKinds.IsText(path))
        {
            if (content.Length > DepotUploadKinds.MaxInlineBytes)
            {
                return DepotBrowserAnswer<bool>.Failure(DepotBrowserStatus.Error, $"{name} is over 8 MiB, which is the most Depot takes as a document.");
            }

            using var reader = new StreamReader(content, Encoding.UTF8);
            var text = await _InvokeAsync<DepotBrowserUpload>(
                DepotBrowserChannel.Upload, request with { Content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(true) }, cancellationToken).ConfigureAwait(true);
            return text.Status == DepotBrowserStatus.Ok
                ? DepotBrowserAnswer<bool>.Success(true)
                : DepotBrowserAnswer<bool>.Failure(text.Status, text.Message);
        }

        var type = _ContentTypes.GetValueOrDefault(Path.GetExtension(name).ToLowerInvariant(), "application/octet-stream");
        var ticket = await _InvokeAsync<DepotBrowserUpload>(
            DepotBrowserChannel.Upload, request with { ContentType = type, Size = content.Length }, cancellationToken).ConfigureAwait(true);
        if (ticket.Data?.UploadUrl is not { } url)
        {
            return DepotBrowserAnswer<bool>.Failure(ticket.Status, ticket.Message ?? "Depot did not return an upload URL.");
        }

        try
        {
            using var body = new StreamContent(content);
            body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            using var response = await http.PutAsync(url, body, cancellationToken).ConfigureAwait(true);
            return response.IsSuccessStatusCode
                ? DepotBrowserAnswer<bool>.Success(true)
                : DepotBrowserAnswer<bool>.Failure(DepotBrowserStatus.Error, $"Depot storage refused {name} (HTTP {(int)response.StatusCode}).");
        }
        catch (HttpRequestException)
        {
            return DepotBrowserAnswer<bool>.Failure(DepotBrowserStatus.Error, "Could not reach Depot storage.");
        }
    }

    private static readonly Dictionary<string, string> _ContentTypes = new()
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".pdf"] = "application/pdf", [".zip"] = "application/zip", [".html"] = "text/html",
    };

    // Only the operator's Sign-in click gets here; nothing else opens a browser.
    public async Task<bool> SignInAsync(CancellationToken cancellationToken = default) =>
        await host.SignInMcpServerAsync(connection.McpServerName, cancellationToken).ConfigureAwait(true) == PluginMcpSignInOutcome.Authorized;

    // The response is open on its headers only, so the body streams to wherever it goes. The URL never goes into a message.
    private async Task<DepotBrowserAnswer<HttpResponseMessage>> _FetchAsync(string project, string path, CancellationToken cancellationToken)
    {
        var url = await _InvokeAsync<string>(DepotBrowserChannel.DownloadUrl, new(connection.Id, project, path), cancellationToken).ConfigureAwait(true);
        if (url.Data is null)
        {
            return DepotBrowserAnswer<HttpResponseMessage>.Failure(url.Status, url.Message);
        }

        try
        {
            var response = await http.GetAsync(url.Data, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(true);
            if (response.IsSuccessStatusCode)
            {
                return DepotBrowserAnswer<HttpResponseMessage>.Success(response);
            }

            var status = (int)response.StatusCode;
            response.Dispose();
            return DepotBrowserAnswer<HttpResponseMessage>.Failure(
                DepotBrowserStatus.Error,
                status is 401 or 403 or 404 or 410
                    ? $"The download link has expired or is no longer allowed (HTTP {status}). Try again."
                    : $"Depot storage answered HTTP {status}.");
        }
        catch (HttpRequestException)
        {
            return DepotBrowserAnswer<HttpResponseMessage>.Failure(DepotBrowserStatus.Error, "Could not reach Depot storage.");
        }
    }

    private async Task<DepotBrowserAnswer<T>> _InvokeAsync<T>(string action, DepotBrowserRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await host.Channel.InvokeAsync(
                action, JsonSerializer.SerializeToElement(request, DepotChannel.Json), cancellationToken).ConfigureAwait(true);
            return answer.Deserialize<DepotBrowserAnswer<T>>(DepotChannel.Json)
                ?? DepotBrowserAnswer<T>.Failure(DepotBrowserStatus.Error, "Depot returned an empty answer.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return DepotBrowserAnswer<T>.Failure(DepotBrowserStatus.Error, exception.Message);
        }
    }
}
