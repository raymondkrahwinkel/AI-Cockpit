using System.Net;
using System.Text.Json;
using Cockpit.Plugins.Abstractions.Files;

namespace Cockpit.Infrastructure.BackendApi;

// AC-2285: one server project's files (or one of its worktrees') as an ordinary file source, the contract the remote
// window's browser and editor plug into (AC-2283, AC-2286). Ids are relative paths; nothing here touches a local path.
public sealed class RemoteProjectFileSource(BackendApiClient client, string projectId, string? worktree = null) : IFileSource
{
    private const int MaxBytes = 1024 * 1024;

    public FileSourceCapabilities Capabilities { get; } = new(CanWrite: true, MaxWriteBytes: MaxBytes) { CanCreateFile = true };

    public Task<FileReadResult> ReadAsync(FileRef file, CancellationToken cancellationToken) => _RunAsync(async () =>
    {
        var (bytes, revision) = await client.GetBytesAsync(_Route("file", file.Id), cancellationToken).ConfigureAwait(false);
        return new FileReadResult(new MemoryStream(bytes, writable: false), revision ?? string.Empty, bytes.Length) { CanWrite = true };
    });

    public Task<FileWriteResult> WriteAsync(FileRef target, Stream content, string? ifVersion, bool mustNotExist, CancellationToken cancellationToken) =>
        _RunAsync(async () =>
        {
            FileSourceChannel.ValidateWrite(ifVersion, mustNotExist);
            try
            {
                return new FileWriteResult(FileWriteStatus.Written, await _PutAsync(target.Id, content, ifVersion, cancellationToken).ConfigureAwait(false));
            }
            catch (BackendApiException error) when (error.Status == HttpStatusCode.Conflict && error.ErrorCode == "conflict")
            {
                return new FileWriteResult(FileWriteStatus.Conflict, null);
            }
        });

    public Task<FileListing> ListAsync(FileRef folder, string? pageToken, CancellationToken cancellationToken) => _RunAsync(async () =>
    {
        var listing = await client.GetAsync<JsonElement>(_Route("files", folder.Id), cancellationToken).ConfigureAwait(false);
        var parent = _Relative(folder.Id);
        var entries = listing.GetProperty("entries").EnumerateArray().Select(entry =>
        {
            var name = entry.GetProperty("name").GetString() ?? string.Empty;
            var id = parent.Length == 0 ? name : $"{parent}/{name}";
            return entry.GetProperty("kind").GetString() == "directory"
                ? new FileEntry(id, name, id, FileEntryKind.Folder)
                : new FileEntry(id, name, id, FileEntryKind.File, entry.GetProperty("size").GetInt64());
        });
        return new FileListing([.. entries]);
    });

    public Task<FileEntry> CreateFileAsync(FileRef parent, string name, Stream content, CancellationToken cancellationToken) => _RunAsync(async () =>
    {
        if (name.Length == 0 || name.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new FileSourceException(FileSourceErrorKind.Forbidden, "A file name cannot contain a folder.");
        }

        var folder = _Relative(parent.Id);
        var id = folder.Length == 0 ? name : $"{folder}/{name}";
        var revision = await _PutAsync(id, content, null, cancellationToken).ConfigureAwait(false);
        return new FileEntry(id, name, id, FileEntryKind.File, content.CanSeek ? content.Length : null, Version: revision);
    });

    public Task MoveAsync(FileRef file, string newPath, string revision, CancellationToken cancellationToken) => _RunAsync(() =>
        client.SendAsync<JsonElement>(HttpMethod.Post, _Route("file/move", null),
            new { from = _Relative(file.Id), to = _Relative(newPath), revision }, cancellationToken));

    public Task DeleteAsync(FileRef file, string revision, CancellationToken cancellationToken) => _RunAsync(async () =>
    {
        await client.DeleteAsync($"{_Route("file", file.Id)}&revision={Uri.EscapeDataString(revision)}", cancellationToken).ConfigureAwait(false);
        return true;
    });

    private async Task<string> _PutAsync(string id, Stream content, string? ifVersion, CancellationToken cancellationToken)
    {
        var bytes = await FileSourceChannel.ReadContentAsync(content, MaxBytes, cancellationToken).ConfigureAwait(false);
        var body = ifVersion is null
            ? (object)new { content = Convert.ToBase64String(bytes), mustNotExist = true }
            : new { content = Convert.ToBase64String(bytes), revision = ifVersion };
        var saved = await client.SendAsync<JsonElement>(HttpMethod.Put, _Route("file", id), body, cancellationToken).ConfigureAwait(false);
        return saved.GetProperty("revision").GetString() ?? string.Empty;
    }

    private string _Route(string what, string? id)
    {
        var query = new List<string>();
        if (id is not null)
        {
            query.Add($"path={Uri.EscapeDataString(_Relative(id) is { Length: > 0 } path ? path : ".")}");
        }

        if (worktree is not null)
        {
            query.Add($"worktree={Uri.EscapeDataString(worktree)}");
        }

        var route = $"api/v1/projects/{Uri.EscapeDataString(projectId)}/{what}";
        return query.Count == 0 ? route : $"{route}?{string.Join('&', query)}";
    }

    // The browser's root is "/"; the server wants paths relative to the project.
    private static string _Relative(string id) => id.Trim('/');

    private static async Task<T> _RunAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (BackendApiException error)
        {
            throw new FileSourceException(error.Status switch
            {
                HttpStatusCode.NotFound => FileSourceErrorKind.NotFound,
                HttpStatusCode.Unauthorized => FileSourceErrorKind.SignIn,
                HttpStatusCode.Forbidden or HttpStatusCode.BadRequest => FileSourceErrorKind.Forbidden,
                HttpStatusCode.RequestEntityTooLarge => FileSourceErrorKind.TooLarge,
                HttpStatusCode.Conflict => FileSourceErrorKind.Conflict,
                _ => FileSourceErrorKind.Unavailable,
            }, error.Description);
        }
        catch (HttpRequestException error)
        {
            throw new FileSourceException(FileSourceErrorKind.Unavailable, error.Message);
        }
    }
}
