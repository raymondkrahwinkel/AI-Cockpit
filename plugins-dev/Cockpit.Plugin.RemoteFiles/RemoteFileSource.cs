using Cockpit.Plugins.Abstractions.Files;
using Wire = Cockpit.Plugins.Abstractions.Files.FileSourceChannel;

namespace Cockpit.Plugin.RemoteFiles;

// Ids are '/'-separated paths under the login folder; mtime+size misses a same-size write within the server's clock tick.
// ponytail: one connection per call; keep a session per connection if browsing large folders feels slow.
internal sealed class RemoteFileSource(bool writable, Func<CancellationToken, Task<IRemoteSession>> connect) : IFileSource
{
    private const string TempMarker = ".cockpit-tmp-";

    public FileSourceCapabilities Capabilities { get; } =
        new(writable, Wire.MaxContentBytes) { CanCreateFolder = writable, CanCreateFile = writable };

    public Task<FileListing> ListAsync(FileRef folder, string? pageToken, CancellationToken cancellationToken) =>
        _RunAsync(async session =>
        {
            var items = await session.ListAsync(_Path(session, folder.Id), cancellationToken).ConfigureAwait(false);
            return new FileListing([.. items.Where(item => _IsName(item.Name)).Select(item => _Entry(folder.Id, item))]);
        }, cancellationToken);

    public Task<FileReadResult> ReadAsync(FileRef file, CancellationToken cancellationToken) =>
        _ReadAsync(file, 0, Wire.MaxContentBytes, whole: true, cancellationToken);

    public Task<FileReadResult> ReadRangeAsync(FileRef file, long offset, int count, CancellationToken cancellationToken) =>
        offset < 0 || count < 1
            ? Task.FromException<FileReadResult>(new FileSourceException(FileSourceErrorKind.Unsupported, "A block starts at 0 or later and holds at least 1 byte."))
            : _ReadAsync(file, offset, count, whole: false, cancellationToken);

    private Task<FileReadResult> _ReadAsync(FileRef file, long offset, int count, bool whole, CancellationToken cancellationToken) =>
        _RunAsync(async session =>
        {
            var path = _Path(session, file.Id);
            var before = await _FileAsync(session, path, cancellationToken).ConfigureAwait(false);
            if (whole && before.Size > Wire.MaxContentBytes)
            {
                throw new FileSourceException(FileSourceErrorKind.TooLarge, "This file is larger than 1 MiB. Download it instead.");
            }
            var bytes = offset >= before.Size ? [] : await session.ReadAsync(path, offset, (int)Math.Min(count, before.Size - offset), cancellationToken).ConfigureAwait(false);
            if ((await _FileAsync(session, path, cancellationToken).ConfigureAwait(false)).Version != before.Version)
            {
                throw new FileSourceException(FileSourceErrorKind.Unavailable, "The file changed while it was read. Try again.");
            }
            return new FileReadResult(new MemoryStream(bytes, writable: false), before.Version, before.Size, before.Modified) { CanWrite = writable };
        }, cancellationToken);

    public async Task<FileWriteResult> WriteAsync(FileRef target, Stream content, string? ifVersion, bool mustNotExist,
        CancellationToken cancellationToken)
    {
        Wire.ValidateWrite(ifVersion, mustNotExist);
        _Writable();
        var bytes = await Wire.ReadContentAsync(content, Wire.MaxContentBytes, cancellationToken).ConfigureAwait(false);
        return await _RunAsync(async session =>
        {
            var path = _Path(session, target.Id);
            if (await _VersionAsync(session, path, cancellationToken).ConfigureAwait(false) is var current && (mustNotExist ? current is not null : current != ifVersion))
            {
                return new FileWriteResult(FileWriteStatus.Conflict, current);
            }
            return await _CommitAsync(session, path, bytes, overwriteVersion: mustNotExist ? null : ifVersion, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<FileEntry> CreateFolderAsync(FileRef parent, string name, CancellationToken cancellationToken) =>
        _RunAsync(async session =>
        {
            _Writable();
            var path = _Path(session, _Child(parent.Id, name));
            if (await session.StatAsync(path, cancellationToken).ConfigureAwait(false) is not null)
            {
                throw _Exists();
            }
            await session.CreateFolderAsync(path, cancellationToken).ConfigureAwait(false);
            return new FileEntry(_Child(parent.Id, name), name, _Child(parent.Id, name), FileEntryKind.Folder);
        }, cancellationToken);

    public async Task<FileEntry> CreateFileAsync(FileRef parent, string name, Stream content, CancellationToken cancellationToken)
    {
        _Writable();
        var id = _Child(parent.Id, name);
        var bytes = await Wire.ReadContentAsync(content, Wire.MaxContentBytes, cancellationToken).ConfigureAwait(false);
        return await _RunAsync(async session =>
        {
            var path = _Path(session, id);
            if (await session.StatAsync(path, cancellationToken).ConfigureAwait(false) is not null
                || (await _CommitAsync(session, path, bytes, overwriteVersion: null, cancellationToken).ConfigureAwait(false)).Status == FileWriteStatus.Conflict)
            {
                throw _Exists();
            }
            return _Entry(parent.Id, await _FileAsync(session, path, cancellationToken).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);
    }

    // Writes beside the target and renames only after the version is checked again; null overwriteVersion means create.
    private static async Task<FileWriteResult> _CommitAsync(IRemoteSession session, string path, byte[] bytes, string? overwriteVersion,
        CancellationToken cancellationToken)
    {
        var temp = path + TempMarker + Guid.NewGuid().ToString("n");
        try
        {
            await session.UploadNewAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            if (overwriteVersion is not null && await _VersionAsync(session, path, cancellationToken).ConfigureAwait(false) is var now && now != overwriteVersion)
            {
                return new FileWriteResult(FileWriteStatus.Conflict, now);
            }
            if (!await session.MoveAsync(temp, path, overwrite: overwriteVersion is not null, cancellationToken).ConfigureAwait(false))
            {
                return new FileWriteResult(FileWriteStatus.Conflict, await _VersionAsync(session, path, cancellationToken).ConfigureAwait(false));
            }
            return new FileWriteResult(FileWriteStatus.Written, (await _FileAsync(session, path, cancellationToken).ConfigureAwait(false)).Version);
        }
        finally
        {
            await _DeleteQuietlyAsync(session, temp).ConfigureAwait(false);
        }
    }

    // A temp file that cannot be removed must not turn a finished save into a failure.
    private static async Task _DeleteQuietlyAsync(IRemoteSession session, string temp)
    {
        try
        {
            if (await session.StatAsync(temp, CancellationToken.None).ConfigureAwait(false) is not null)
            {
                await session.DeleteAsync(temp, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
        }
    }

    private static async Task<string?> _VersionAsync(IRemoteSession session, string path, CancellationToken cancellationToken) =>
        (await session.StatAsync(path, cancellationToken).ConfigureAwait(false)) is { Folder: false } item ? item.Version : null;

    private static async Task<RemoteItem> _FileAsync(IRemoteSession session, string path, CancellationToken cancellationToken) =>
        await session.StatAsync(path, cancellationToken).ConfigureAwait(false) switch
        {
            null => throw new FileSourceException(FileSourceErrorKind.NotFound, "This item does not exist on the server."),
            { Folder: true } => throw new FileSourceException(FileSourceErrorKind.Unsupported, "This is a folder, not a file."),
            var file => file,
        };

    private async Task<T> _RunAsync<T>(Func<IRemoteSession, Task<T>> action, CancellationToken cancellationToken)
    {
        await using var session = await connect(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(session).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw RemoteTrust.Translate(error);
        }
    }

    private void _Writable()
    {
        if (!writable)
        {
            throw new FileSourceException(FileSourceErrorKind.Forbidden, "This connection is read only.");
        }
    }

    private FileEntry _Entry(string parent, RemoteItem item) => new(_Child(parent, item.Name), item.Name, _Child(parent, item.Name),
        item.Folder ? FileEntryKind.Folder : FileEntryKind.File, item.Folder ? null : item.Size,
        item.Modified == DateTimeOffset.MinValue ? null : item.Modified, item.Folder ? null : item.Version);

    private static string _Child(string parent, string name) => _IsName(name)
        ? (parent is "/" or "" ? name : parent.TrimEnd('/') + "/" + name)
        : throw new FileSourceException(FileSourceErrorKind.Unsupported, "This name cannot be used on the server.");

    // The server's own permissions are the boundary; this only keeps an id from climbing out of the login folder.
    private static string _Path(IRemoteSession session, string id)
    {
        var segments = id == "/" ? [] : id.Split('/');
        if (segments.Any(segment => !_IsName(segment)))
        {
            throw new FileSourceException(FileSourceErrorKind.Forbidden, "This path is outside the connection's folder.");
        }
        return string.Join('/', [session.Home.TrimEnd('/'), .. segments]) is { Length: > 0 } path ? path : "/";
    }

    private static bool _IsName(string name) =>
        name.Length > 0 && name is not ("." or "..") && name.IndexOfAny(['/', '\\', '\0']) < 0 && !name.Contains(TempMarker, StringComparison.Ordinal);

    private static FileSourceException _Exists() => new(FileSourceErrorKind.Conflict, "An item with this name already exists.");
}
