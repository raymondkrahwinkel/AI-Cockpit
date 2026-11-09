using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Cockpit.Plugin.RemoteFiles.Contracts;
using Cockpit.Plugins.Abstractions.Files;

namespace Cockpit.Plugin.RemoteFiles;

internal sealed class WebDavSession : IRemoteSession
{
    private static readonly XNamespace Dav = "DAV:";
    private readonly HttpClient _client;
    private readonly Uri _root;
    private readonly RemoteTrust _trust;
    private readonly AuthenticationHeaderValue _authorization;

    public WebDavSession(RemoteConnection connection, RemoteSecrets secrets, RemoteTrust trust, HttpMessageHandler? handler = null)
    {
        _root = Root(connection.DavUrl) ?? throw new FileSourceException(FileSourceErrorKind.Unsupported, "Enter an HTTP or HTTPS WebDAV folder URL without credentials or a query.");
        _trust = trust;
        _authorization = connection.UseBearer
            ? new AuthenticationHeaderValue("Bearer", secrets.Password ?? "")
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(connection.User + ":" + secrets.Password)));
        _client = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => trust.CheckCertificate(certificate, errors),
        }) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public string Home => "/";

    internal static bool HasStrongETag(string version) => EntityTagHeaderValue.TryParse(version, out var etag) && !etag.IsWeak;

    internal static Uri? Root(string url) =>
        !Uri.UnescapeDataString(url).Any(char.IsControl) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? new Uri(uri.AbsoluteUri.TrimEnd('/') + "/") : null;

    public async Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken) =>
        [.. (await _PropFindAsync(path, "1", cancellationToken).ConfigureAwait(false))
            .Where(entry => entry.Path != path.TrimEnd('/') && _Parent(entry.Path) == path.TrimEnd('/')).Select(entry => entry.Item)];

    public async Task<RemoteItem?> StatAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return (await _PropFindAsync(path, "0", cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(entry => entry.Path == path.TrimEnd('/')).Item;
        }
        catch (FileSourceException error) when (error.Kind == FileSourceErrorKind.NotFound)
        {
            return null;
        }
    }

    private async Task<List<(string Path, RemoteItem Item)>> _PropFindAsync(string path, string depth, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), _Uri(depth == "1" ? path.TrimEnd('/') + "/" : path));
        request.Headers.Add("Depth", depth);
        request.Content = new StringContent("<d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/><d:getetag/></d:prop></d:propfind>", Encoding.UTF8, "application/xml");
        using var response = await _SendAsync(request, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        var entries = new List<(string, RemoteItem)>();
        foreach (var entry in document.Descendants(Dav + "response"))
        {
            var href = (string?)entry.Element(Dav + "href");
            if (!Uri.TryCreate(request.RequestUri, href, out var uri) || !_root.IsBaseOf(uri))
            {
                continue;
            }
            var relative = Uri.UnescapeDataString(_root.MakeRelativeUri(uri).ToString()).TrimEnd('/');
            var decodedPath = "/" + relative;
            var prop = entry.Elements(Dav + "propstat")
                .FirstOrDefault(part => ((string?)part.Element(Dav + "status"))?.Split(' ').ElementAtOrDefault(1) == "200")?.Element(Dav + "prop");
            if (prop is null)
            {
                continue;
            }
            long.TryParse((string?)prop.Element(Dav + "getcontentlength"), NumberStyles.None, CultureInfo.InvariantCulture, out var size);
            DateTimeOffset.TryParse((string?)prop.Element(Dav + "getlastmodified"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var modified);
            entries.Add((decodedPath.TrimEnd('/'), new RemoteItem(relative[(relative.LastIndexOf('/') + 1)..],
                prop.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null, size, modified, (string?)prop.Element(Dav + "getetag"))));
        }
        return entries;
    }

    public async Task<byte[]> ReadAsync(string path, long offset, int count, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _Uri(path));
        request.Headers.Range = new RangeHeaderValue(offset, checked(offset + count - 1));
        using var response = await _SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From != offset)
        {
            throw new FileSourceException(FileSourceErrorKind.Unavailable, "The server returned a different file block. Try again.");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.PartialContent && offset > 0)
        {
            var skip = new byte[8192];
            while (offset > 0)
            {
                var read = await stream.ReadAsync(skip.AsMemory(0, (int)Math.Min(skip.Length, offset)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return [];
                }
                offset -= read;
            }
        }
        var bytes = new byte[count];
        var length = await stream.ReadAtLeastAsync(bytes, count, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        return bytes[..length];
    }

    public async Task<FileWriteResult> PutAsync(string path, byte[] content, string? version, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, _Uri(path));
        request.Content = new ByteArrayContent(content);
        if (version is null)
        {
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        }
        else if (EntityTagHeaderValue.TryParse(version, out var etag) && !etag.IsWeak)
        {
            request.Headers.IfMatch.Add(etag);
        }
        else
        {
            // AC-1788: a timestamp/size fallback cannot provide an atomic If-Match; refuse rather than risk lost edits.
            throw new FileSourceException(FileSourceErrorKind.Unsupported, "This server did not provide a strong ETag. Saving cannot safely protect concurrent edits.");
        }
        using var response = await _SendAsync(request, cancellationToken, allowConflict: true).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return new FileWriteResult(FileWriteStatus.Conflict, (await StatAsync(path, cancellationToken).ConfigureAwait(false))?.Version);
        }
        return new FileWriteResult(FileWriteStatus.Written, response.Headers.ETag?.ToString()
            ?? (await StatAsync(path, cancellationToken).ConfigureAwait(false))?.Version);
    }

    public async Task CreateFolderAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), _Uri(path));
        using var response = await _SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> _SendAsync(HttpRequestMessage request, CancellationToken cancellationToken, bool allowConflict = false)
    {
        request.Headers.Authorization = _authorization;
        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw _trust.Refused ? _trust.Refusal() : RemoteTrust.Translate(error);
        }
        if (response.IsSuccessStatusCode || (allowConflict && response.StatusCode == HttpStatusCode.PreconditionFailed))
        {
            return response;
        }
        var errorKind = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => FileSourceErrorKind.SignIn,
            HttpStatusCode.Forbidden => FileSourceErrorKind.Forbidden,
            HttpStatusCode.NotFound => FileSourceErrorKind.NotFound,
            HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.MethodNotAllowed => FileSourceErrorKind.Conflict,
            _ => FileSourceErrorKind.Unavailable,
        };
        response.Dispose();
        throw new FileSourceException(errorKind, errorKind switch
        {
            FileSourceErrorKind.SignIn => "The server refused the sign-in. Check the user name and the password or token.",
            FileSourceErrorKind.Forbidden => "The server refused access to this item.",
            FileSourceErrorKind.NotFound => "This item does not exist on the server.",
            FileSourceErrorKind.Conflict => "The item changed or already exists. Refresh and try again.",
            _ => "The remote file action failed.",
        });
    }

    private Uri _Uri(string path) => new(_root, string.Join('/', path.TrimStart('/').Split('/').Select(Uri.EscapeDataString)));
    private static string _Parent(string path) => path[..path.LastIndexOf('/')];

    public async Task UploadNewAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        if ((await PutAsync(path, content, null, cancellationToken).ConfigureAwait(false)).Status == FileWriteStatus.Conflict)
        {
            throw new FileSourceException(FileSourceErrorKind.Conflict, "An item with this name already exists.");
        }
    }

    public async Task<bool> MoveAsync(string from, string to, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod("MOVE"), _Uri(from));
        request.Headers.Add("Destination", _Uri(to).AbsoluteUri);
        request.Headers.Add("Overwrite", "F");
        using var response = await _SendAsync(request, cancellationToken, allowConflict: true).ConfigureAwait(false);
        return response.StatusCode != HttpStatusCode.PreconditionFailed;
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, _Uri(path));
        using var response = await _SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
