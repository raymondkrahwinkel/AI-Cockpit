using Cockpit.Plugins.Abstractions.Files;
using Wire = Cockpit.Plugins.Abstractions.Files.FileSourceChannel;

namespace Cockpit.Plugin.RemoteFiles;

// Ids are '/'-separated paths under the login folder; mtime+size misses a same-size write within the server's clock tick.
// ponytail: one connection per call; keep a session per connection if browsing large folders feels slow.
internal sealed class RemoteFileSource(bool writable, Func<CancellationToken, Task<IRemoteSession>> connect) : IFileSource
{
    private const string TempMarker = ".cockpit-tmp-";
    private const string BackupMarker = ".cockpit-bak-";
    private const string NotSaved = "The file could not be saved. The server still has the previous version.";

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
    // The old file is parked under a backup name until the new one is in place, so a failed rename never loses both.
    // ponytail: between the two renames the file is briefly absent; posix-rename would close that gap on OpenSSH servers.
    private static async Task<FileWriteResult> _CommitAsync(IRemoteSession session, string path, byte[] bytes, string? overwriteVersion,
        CancellationToken cancellationToken)
    {
        var temp = path + TempMarker + Guid.NewGuid().ToString("n");
        var keepTemp = false;
        try
        {
            await session.UploadNewAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            if (overwriteVersion is null)
            {
                return await session.MoveAsync(temp, path, cancellationToken).ConfigureAwait(false)
                    ? new FileWriteResult(FileWriteStatus.Written, (await _FileAsync(session, path, cancellationToken).ConfigureAwait(false)).Version)
                    : new FileWriteResult(FileWriteStatus.Conflict, await _VersionAsync(session, path, cancellationToken).ConfigureAwait(false));
            }
            if (await _VersionAsync(session, path, cancellationToken).ConfigureAwait(false) is var now && now != overwriteVersion)
            {
                return new FileWriteResult(FileWriteStatus.Conflict, now);
            }
            var backup = path + BackupMarker + Guid.NewGuid().ToString("n");
            if (!await session.MoveAsync(path, backup, cancellationToken).ConfigureAwait(false))
            {
                throw new FileSourceException(FileSourceErrorKind.Unavailable, NotSaved);
            }
            // Past this point the old file is parked, so nothing may cancel before it is replaced or put back.
            if (!await _TryMoveAsync(session, temp, path).ConfigureAwait(false))
            {
                keepTemp = !await _TryMoveAsync(session, backup, path).ConfigureAwait(false);
                throw new FileSourceException(FileSourceErrorKind.Unavailable, keepTemp
                    ? $"The save was interrupted. The previous version is on the server as {_Name(backup)} and the new one as {_Name(temp)}."
                    : NotSaved);
            }
            await _DeleteQuietlyAsync(session, backup).ConfigureAwait(false);
            return new FileWriteResult(FileWriteStatus.Written, (await _FileAsync(session, path, CancellationToken.None).ConfigureAwait(false)).Version);
        }
        finally
        {
            if (!keepTemp)
            {
                await _DeleteQuietlyAsync(session, temp).ConfigureAwait(false);
            }
        }
    }

    private static async Task<bool> _TryMoveAsync(IRemoteSession session, string from, string to)
    {
        try
        {
            return await session.MoveAsync(from, to, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string _Name(string path) => path[(path.LastIndexOf('/') + 1)..];

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
        var session = await connect(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(session).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw RemoteTrust.Translate(error);
        }
        finally
        {
            await RemoteSession.CloseQuietlyAsync(session).ConfigureAwait(false);
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

    // Control characters are refused too: a CR or LF in a name would end an FTP command early.
    private static bool _IsName(string name) =>
        name.Length > 0 && name is not ("." or "..") && name.IndexOf('/') < 0 && name.IndexOf('\\') < 0 && !name.Any(char.IsControl)
        && !name.Contains(TempMarker, StringComparison.Ordinal);

    private static FileSourceException _Exists() => new(FileSourceErrorKind.Conflict, "An item with this name already exists.");
}
