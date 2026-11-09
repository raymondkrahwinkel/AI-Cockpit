using Cockpit.Plugins.Abstractions.Files;
using Wire = Cockpit.Plugins.Abstractions.Files.FileSourceChannel;

namespace Cockpit.Plugin.LocalFiles;

// One added folder as a file source. Ids are '/'-separated paths relative to the root; every call resolves them again
// through PathBoundary. Version = mtime+size: a same-size write within one clock tick goes unnoticed.
internal sealed class LocalFileSource(string connectionId, string root, bool writable, IReadOnlyCollection<string> denied) : IFileSource
{
    // ponytail: one lock for every local write in this process; an outside writer between the last check and the
    // rename still wins. Per-file locks if contention shows; a real compare-and-swap needs OS support Windows lacks.
    private static readonly SemaphoreSlim Writing = new(1, 1);

    public FileSourceCapabilities Capabilities { get; } =
        new(writable, Wire.MaxContentBytes) { CanCreateFolder = writable, CanCreateFile = writable };

    public Task<FileListing> ListAsync(FileRef folder, string? pageToken, CancellationToken cancellationToken) => _Run(() =>
    {
        var path = _Resolve(folder);
        if (!Directory.Exists(path))
        {
            throw new FileSourceException(FileSourceErrorKind.NotFound, "This folder does not exist any more.");
        }
        // Names the boundary would refuse cannot be opened, and the state folder stays hidden as well as refused.
        var entries = new DirectoryInfo(path).EnumerateFileSystemInfos()
            .Where(item => PathBoundary.IsName(item.Name) && !denied.Any(folder => PathBoundary.IsWithin(folder, item.FullName)))
            .Select(item => _Entry(folder.Id, item))
            .ToList();
        return new FileListing(entries);
    });

    public Task<FileReadResult> ReadAsync(FileRef file, CancellationToken cancellationToken) =>
        ReadRangeAsync(file, 0, Wire.MaxContentBytes + 1, cancellationToken);

    public Task<FileReadResult> ReadRangeAsync(FileRef file, long offset, int count, CancellationToken cancellationToken) => _Run(() =>
    {
        using var stream = new FileStream(_Resolve(file), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var before = _Version(stream);
        var buffer = new byte[(int)Math.Clamp(stream.Length - offset, 0, count)];
        var read = 0;
        while (read < buffer.Length && RandomAccess.Read(stream.SafeFileHandle, buffer.AsSpan(read), offset + read) is var n and > 0)
        {
            read += n;
        }
        // Another program writing in place would hand out bytes of two versions under one.
        if (_Version(stream) != before)
        {
            throw new FileSourceException(FileSourceErrorKind.Unavailable, "The file changed while it was read. Try again.");
        }
        return new FileReadResult(new MemoryStream(buffer, 0, read, writable: false), before, stream.Length,
            File.GetLastWriteTimeUtc(stream.SafeFileHandle)) { CanWrite = writable };
    });

    public async Task<FileWriteResult> WriteAsync(FileRef target, Stream content, string? ifVersion, bool mustNotExist,
        CancellationToken cancellationToken)
    {
        Wire.ValidateWrite(ifVersion, mustNotExist);
        var path = _Writable(target);
        if (mustNotExist)
        {
            return await _CreateAsync(path, content, cancellationToken).ConfigureAwait(false) is { } created
                ? new FileWriteResult(FileWriteStatus.Written, created)
                : new FileWriteResult(FileWriteStatus.Conflict, _VersionOf(path));
        }
        await Writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temp = _Temp(path);
        try
        {
            if (_VersionOf(path) is var current && current != ifVersion)
            {
                return new FileWriteResult(FileWriteStatus.Conflict, current);
            }
            var version = await _WriteTempAsync(temp, content, cancellationToken).ConfigureAwait(false);
            if (_VersionOf(path) is var now && now != ifVersion)
            {
                return new FileWriteResult(FileWriteStatus.Conflict, now);
            }
            File.Move(temp, path, overwrite: true);
            return new FileWriteResult(FileWriteStatus.Written, version);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw _Failed(error);
        }
        finally
        {
            File.Delete(temp);
            Writing.Release();
        }
    }

    public Task<FileEntry> CreateFolderAsync(FileRef parent, string name, CancellationToken cancellationToken) => _Run(() =>
    {
        var child = _Child(parent, name);
        var path = _Writable(child);
        if (Path.Exists(path))
        {
            throw _Exists();
        }
        return _Entry(parent.Id, Directory.CreateDirectory(path));
    });

    public async Task<FileEntry> CreateFileAsync(FileRef parent, string name, Stream content, CancellationToken cancellationToken)
    {
        var child = _Child(parent, name);
        var path = _Writable(child);
        _ = await _CreateAsync(path, content, cancellationToken).ConfigureAwait(false) ?? throw _Exists();
        return _Entry(parent.Id, new FileInfo(path));
    }

    // Null when the name is taken: the move without overwrite is the atomic "must not exist" check.
    private static async Task<string?> _CreateAsync(string path, Stream content, CancellationToken cancellationToken)
    {
        var temp = _Temp(path);
        try
        {
            var version = await _WriteTempAsync(temp, content, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: false);
            return version;
        }
        catch (IOException) when (Path.Exists(path))
        {
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw _Failed(error);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    // The version is read after closing, since NTFS may stamp the write time only when the handle closes.
    private static async Task<string> _WriteTempAsync(string temp, Stream content, CancellationToken cancellationToken)
    {
        var bytes = await Wire.ReadContentAsync(content, Wire.MaxContentBytes, cancellationToken).ConfigureAwait(false);
        await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        return _VersionOf(temp) ?? throw new FileSourceException(FileSourceErrorKind.Unavailable, "The file could not be saved.");
    }

    private static string _Temp(string path) => path + PathBoundary.TempMarker + Guid.NewGuid().ToString("n");

    private string _Writable(FileRef file) => writable
        ? _Resolve(file)
        : throw new FileSourceException(FileSourceErrorKind.Forbidden, "This folder is read only.");

    private FileRef _Child(FileRef parent, string name) => PathBoundary.IsName(name)
        ? new FileRef(connectionId, _Id(parent.Id, name), _Id(parent.Id, name))
        : throw new FileSourceException(FileSourceErrorKind.Unsupported, "This name cannot be used for a file or folder.");

    // Id and Path are the same relative path; a reference whose two halves disagree was not made by this source.
    private string _Resolve(FileRef file)
    {
        if (file.ConnectionId != connectionId || file.Id != file.Path)
        {
            throw PathBoundary.Outside();
        }
        if (!Directory.Exists(root))
        {
            throw new FileSourceException(FileSourceErrorKind.Unavailable,
                $"{root} does not exist any more, or its drive is not connected. The folder stays in the settings until you remove it.");
        }
        // A root that became a link, or lies in a denied folder, is not the folder the operator added.
        if (!PathBoundary.RealPath(root).Equals(root, PathBoundary.Comparison) || denied.Any(folder => PathBoundary.IsWithin(folder, root)))
        {
            throw PathBoundary.Outside();
        }
        return PathBoundary.Resolve(root, file.Id, denied);
    }

    private FileEntry _Entry(string parentId, FileSystemInfo item)
    {
        var id = _Id(parentId, item.Name);
        return item.Attributes.HasFlag(FileAttributes.Directory)
            ? new FileEntry(id, item.Name, id, FileEntryKind.Folder, Modified: item.LastWriteTimeUtc)
            : new FileEntry(id, item.Name, id, FileEntryKind.File, ((FileInfo)item).Length, item.LastWriteTimeUtc,
                _Version(item.LastWriteTimeUtc, ((FileInfo)item).Length));
    }

    private static string _Id(string parentId, string name) => parentId == "/" ? name : $"{parentId}/{name}";

    private static string? _VersionOf(string path) => new FileInfo(path) is { Exists: true } file ? _Version(file.LastWriteTimeUtc, file.Length) : null;

    private static string _Version(FileStream stream) => _Version(File.GetLastWriteTimeUtc(stream.SafeFileHandle), stream.Length);

    private static string _Version(DateTime modified, long size) => $"{modified.Ticks}+{size}";

    private static FileSourceException _Exists() => new(FileSourceErrorKind.Conflict, "An item with this name already exists.");

    // Fixed sentences only: an exception's own text names paths and accounts.
    private static FileSourceException _Failed(Exception error) => error switch
    {
        FileNotFoundException or DirectoryNotFoundException => new(FileSourceErrorKind.NotFound, "This item does not exist any more."),
        UnauthorizedAccessException => new(FileSourceErrorKind.Forbidden, "Forbidden: the system refused access to this item."),
        _ => new(FileSourceErrorKind.Unavailable, "The file system could not complete this operation."),
    };

    private static Task<T> _Run<T>(Func<T> action)
    {
        try
        {
            return Task.FromResult(action());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Task.FromException<T>(_Failed(error));
        }
        catch (FileSourceException error)
        {
            return Task.FromException<T>(error);
        }
    }
}
