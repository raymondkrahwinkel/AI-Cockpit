using System.Text;
using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Depot.UI;

internal sealed record DepotDocumentExcerpt(string? Heading, string? Paragraph);

// AC-1568: the file browser's behaviour for one Depot connection, apart from the controls that show it. Every Depot
// call goes to the backend part over the channel; the only network this part does itself is the pre-signed URL.
internal sealed class DepotBrowserModel(ICockpitUiHost host, DepotConnectionRegistration connection, HttpClient http)
{
    public DepotConnectionRegistration Connection => connection;

    public Task<DepotBrowserAnswer<IReadOnlyList<DepotBrowserProject>>> ProjectsAsync(CancellationToken cancellationToken = default) =>
        _InvokeAsync<IReadOnlyList<DepotBrowserProject>>(DepotBrowserChannel.Projects, new(connection.Id), cancellationToken);

    public Task<DepotBrowserAnswer<DepotBrowserTree>> TreeAsync(string project, CancellationToken cancellationToken = default) =>
        _InvokeAsync<DepotBrowserTree>(DepotBrowserChannel.Tree, new(connection.Id, project), cancellationToken);

    public async Task<DepotBrowserAnswer<DepotDocumentExcerpt>> ReadDocumentExcerptAsync(string project, DepotBrowserNode node, CancellationToken cancellationToken = default)
    {
        var read = await _InvokeAsync<string>(DepotBrowserChannel.Read, new(connection.Id, project, node.Path), cancellationToken).ConfigureAwait(true);
        return read.Status == DepotBrowserStatus.Ok
            ? DepotBrowserAnswer<DepotDocumentExcerpt>.Success(_Excerpt(read.Data ?? string.Empty))
            : DepotBrowserAnswer<DepotDocumentExcerpt>.Failure(read.Status, read.Message);
    }

    private static DepotDocumentExcerpt _Excerpt(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = 0;
        if (lines.FirstOrDefault()?.Trim() == "---")
        {
            var end = Array.FindIndex(lines, 1, line => line.Trim() == "---");
            start = end < 0 ? 0 : end + 1;
        }

        string? heading = null;
        string? paragraph = null;
        var inFence = false;
        for (var index = start; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (_IsFence(line))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            if (heading is null && _IsHeading(line))
            {
                heading = line[(line.IndexOf(' ') + 1)..].Trim();
                continue;
            }

            if (line.Length == 0 || _StartsBlock(line))
            {
                continue;
            }

            var parts = new List<string> { line };
            for (index++; index < lines.Length; index++)
            {
                var continuation = lines[index].Trim();
                if (continuation.Length == 0 || _StartsBlock(continuation))
                {
                    break;
                }

                parts.Add(continuation);
            }

            paragraph = string.Join(' ', parts);
            break;
        }

        return new(heading, paragraph);
    }

    private static bool _StartsBlock(string line) =>
        _IsHeading(line) || _IsFence(line) || line.StartsWith("- ", StringComparison.Ordinal)
        || line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith(">", StringComparison.Ordinal)
        || line.StartsWith("|", StringComparison.Ordinal) || line.StartsWith("<", StringComparison.Ordinal)
        || _IsOrderedListItem(line);

    private static bool _IsHeading(string line)
    {
        var markerLength = line.TakeWhile(character => character == '#').Count();
        return markerLength is >= 1 and <= 3 && line.Length > markerLength && line[markerLength] == ' ';
    }

    private static bool _IsFence(string line) =>
        line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal);

    private static bool _IsOrderedListItem(string line)
    {
        var marker = line.IndexOf(". ", StringComparison.Ordinal);
        return marker > 0 && line[..marker].All(char.IsDigit);
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

            string body;
            try
            {
                using var reader = new StreamReader(content, new UTF8Encoding(false, throwOnInvalidBytes: true));
                body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (DecoderFallbackException)
            {
                return DepotBrowserAnswer<bool>.Failure(DepotBrowserStatus.Error, $"{name} is not UTF-8 text, which is the only text Depot stores as a document.");
            }

            var text = await _InvokeAsync<DepotBrowserUpload>(DepotBrowserChannel.Upload, request with { Content = body }, cancellationToken).ConfigureAwait(true);
            return text.Status == DepotBrowserStatus.Ok
                ? DepotBrowserAnswer<bool>.Success(true)
                : DepotBrowserAnswer<bool>.Failure(text.Status, text.Message);
        }

        var type = DepotUploadKinds.ContentType(name);
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
        catch (Exception exception) when (exception is HttpRequestException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return DepotBrowserAnswer<bool>.Failure(DepotBrowserStatus.Error, "Could not reach Depot storage, or it took too long.");
        }
    }

    public Task<DepotBrowserAnswer<IReadOnlyList<DepotPublicationRow>>> PublicationsAsync(string project, CancellationToken cancellationToken = default) =>
        _InvokePublishAsync<IReadOnlyList<DepotPublicationRow>>(DepotPublishChannel.List, new(connection.Id, project), cancellationToken);

    public Task<DepotBrowserAnswer<DepotPublication>> AddPublicationAsync(string project, string source, string targetFolder, CancellationToken cancellationToken = default) =>
        _InvokePublishAsync<DepotPublication>(DepotPublishChannel.Add, new(connection.Id, project, Source: source, TargetFolder: targetFolder), cancellationToken);

    public Task<DepotBrowserAnswer<bool>> RemovePublicationAsync(DepotPublication link, CancellationToken cancellationToken = default) =>
        _InvokePublishAsync<bool>(DepotPublishChannel.Remove, new(connection.Id, link.Project, link.Id), cancellationToken);

    // Plan, then the operator's yes, then the run. Files that changed in Depot since the last publication are a second,
    // separate question; "no" leaves them alone. Null when the operator declined the plan.
    public async Task<DepotBrowserAnswer<DepotPublishReport>?> PublishAsync(DepotPublication link, CancellationToken cancellationToken = default)
    {
        var plan = await _InvokePublishAsync<DepotPublishPlan>(DepotPublishChannel.Plan, new(connection.Id, link.Project, link.Id), cancellationToken).ConfigureAwait(true);
        if (plan.Data is not { } items)
        {
            return DepotBrowserAnswer<DepotPublishReport>.Failure(plan.Status, plan.Message);
        }

        int Count(params string[] actions) => items.Items.Count(item => actions.Contains(item.Action));
        var writes = Count(DepotPublishAction.New, DepotPublishAction.Changed);
        var removals = Count(DepotPublishAction.Delete);
        var conflicts = Count(DepotPublishAction.Conflict);
        var overwrite = false;
        if (writes + removals + conflicts > 0)
        {
            var summary = $"{writes} to publish, {removals} to move to Depot's trash, {Count(DepotPublishAction.Unchanged, DepotPublishAction.Adopt)} already there, {Count(DepotPublishAction.Skipped)} skipped.";
            if (!await host.ConfirmAsync("Publish to Depot?", $"{link.Source} → {(link.TargetFolder.Length == 0 ? "the project root" : link.TargetFolder)}: {summary}", "Publish").ConfigureAwait(true))
            {
                return null;
            }

            overwrite = conflicts > 0 && await host.ConfirmAsync(
                "Overwrite changes made in Depot?",
                $"{conflicts} file(s) were changed in Depot since the last publication. Depot keeps the previous version.",
                "Overwrite").ConfigureAwait(true);
        }

        return await _InvokePublishAsync<DepotPublishReport>(
            DepotPublishChannel.Run, new(connection.Id, link.Project, link.Id, Confirmed: true, OverwriteConflicts: overwrite, PlanFingerprint: items.Fingerprint), cancellationToken).ConfigureAwait(true);
    }

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

    private Task<DepotBrowserAnswer<T>> _InvokeAsync<T>(string action, DepotBrowserRequest request, CancellationToken cancellationToken) =>
        _InvokeAnyAsync<T>(action, request, cancellationToken);

    private Task<DepotBrowserAnswer<T>> _InvokePublishAsync<T>(string action, DepotPublishRequest request, CancellationToken cancellationToken) =>
        _InvokeAnyAsync<T>(action, request, cancellationToken);

    private async Task<DepotBrowserAnswer<T>> _InvokeAnyAsync<T>(string action, object request, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await host.Channel.InvokeAsync(
                action, JsonSerializer.SerializeToElement(request, request.GetType(), DepotChannel.Json), cancellationToken).ConfigureAwait(true);
            return answer.Deserialize<DepotBrowserAnswer<T>>(DepotChannel.Json)
                ?? DepotBrowserAnswer<T>.Failure(DepotBrowserStatus.Error, "Depot returned an empty answer.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return DepotBrowserAnswer<T>.Failure(DepotBrowserStatus.Error, exception.Message);
        }
    }
}
