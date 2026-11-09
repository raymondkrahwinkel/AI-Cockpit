using Cockpit.Plugins.Abstractions.Files;
using Wire = Cockpit.Plugins.Abstractions.Files.FileSourceChannel;

namespace Cockpit.Plugin.LocalFiles;

// Ids are '/'-separated paths under the root, resolved again on every call; mtime+size misses a same-size write in one tick.
internal sealed class LocalFileSource(string connectionId, string root, bool writable, IReadOnlyCollection<string> denied) : IFileSource
{
    // ponytail: one process-wide lock and a version check before the rename; per-file locks or real CAS if contention shows.
    private static readonly SemaphoreSlim Writing = new(1, 1);

    // Skipping unreadable items keeps one broken link or locked file from hiding the whole folder.
    private static readonly EnumerationOptions Listing = new() { IgnoreInaccessible = true, AttributesToSkip = 0 };

    public FileSourceCapabilities Capabilities { get; } =
        new(writable, Wire.MaxContentBytes) { CanCreateFolder = writable, CanCreateFile = writable };

    public Task<FileListing> ListAsync(FileRef folder, string? pageToken, CancellationToken cancellationToken) => _Run(() =>
    {
        var path = _Resolve(folder);
        if (!Directory.Exists(path))
        {
            throw new FileSourceException(FileSourceErrorKind.NotFound, "This folder does not exist any more.");
        }
        var entries = new List<FileEntry>();
        foreach (var item in new DirectoryInfo(path).EnumerateFileSystemInfos("*", Listing))
        {
            // Names the boundary refuses cannot be opened, and the state folder stays hidden as well as refused.
            if (PathBoundary.IsName(item.Name) && !denied.Any(hidden => PathBoundary.IsWithin(hidden, item.FullName))
                && _TryEntry(folder.Id, item) is { } entry)
            {
                entries.Add(entry);
            }
        }
        return new FileListing(entries);
    });

    public Task<FileReadResult> ReadAsync(FileRef file, CancellationToken cancellationToken) =>
        _ReadAsync(file, 0, Wire.MaxContentBytes + 1);

    public Task<FileReadResult> ReadRangeAsync(FileRef file, long offset, int count, CancellationToken cancellationToken) =>
        offset < 0 || count < 1
            ? Task.FromException<FileReadResult>(new FileSourceException(FileSourceErrorKind.Unsupported, "A block starts at 0 or later and holds at least 1 byte."))
            : _ReadAsync(file, offset, count);

    private Task<FileReadResult> _ReadAsync(FileRef file, long offset, int count) => _Run(() =>
    {
        using var stream = new FileStream(_Resolve(file), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        _Opened(stream, reading: true);
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
        // Read before the lock, so a slow upload does not hold up every other save.
        var bytes = await Wire.ReadContentAsync(content, Wire.MaxContentBytes, cancellationToken).ConfigureAwait(false);
        if (mustNotExist)
        {
            return _Create(path, bytes, _Opened, cancellationToken) is { } created
                ? new FileWriteResult(FileWriteStatus.Written, created)
                : new FileWriteResult(FileWriteStatus.Conflict, _VersionOf(path));
        }
        await Writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var temp = _Temp(path);
            try
            {
                if (_VersionOf(path) is var current && current != ifVersion)
                {
                    return new FileWriteResult(FileWriteStatus.Conflict, current);
                }
                var version = _WriteTemp(temp, bytes, _Opened, cancellationToken);
                // A new inode would drop the file's permissions on Unix; on Windows the folder's ACL applies.
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temp, File.GetUnixFileMode(path));
                }
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
                _Delete(temp);
            }
        }
        finally
        {
            Writing.Release();
        }
    }

    public Task<FileEntry> CreateFolderAsync(FileRef parent, string name, CancellationToken cancellationToken) => _Run(() =>
    {
        var path = _Writable(_Child(parent, name));
        // CreateDirectory would quietly make missing parents too.
        if (!Directory.Exists(Path.GetDirectoryName(path)))
        {
            throw new FileSourceException(FileSourceErrorKind.NotFound, "This folder does not exist any more.");
        }
        if (Path.Exists(path))
        {
            throw _Exists();
        }
        return _Entry(parent.Id, Directory.CreateDirectory(path));
    });

    public async Task<FileEntry> CreateFileAsync(FileRef parent, string name, Stream content, CancellationToken cancellationToken)
    {
        var path = _Writable(_Child(parent, name));
        var bytes = await Wire.ReadContentAsync(content, Wire.MaxContentBytes, cancellationToken).ConfigureAwait(false);
        _ = _Create(path, bytes, _Opened, cancellationToken) ?? throw _Exists();
        return _Entry(parent.Id, new FileInfo(path));
    }

    // Null when the name is taken: the move without overwrite is the atomic "must not exist" check.
    private static string? _Create(string path, byte[] bytes, Action<FileStream, bool> opened, CancellationToken cancellationToken)
    {
        var temp = _Temp(path);
        try
        {
            var version = _WriteTemp(temp, bytes, opened, cancellationToken);
            try
            {
                File.Move(temp, path, overwrite: false);
            }
            catch (IOException) when (Path.Exists(path))
            {
                return null;
            }
            return version;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw _Failed(error);
        }
        finally
        {
            _Delete(temp);
        }
    }

    // The version is read after closing, since NTFS may stamp the write time only when the handle closes.
    private static string _WriteTemp(string temp, byte[] bytes, Action<FileStream, bool> opened, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            // The rename stays in this folder, so checking the temp file also covers the file it replaces.
            opened(stream, false);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        return _VersionOf(temp) ?? throw new FileSourceException(FileSourceErrorKind.Unavailable, "The file could not be saved.");
    }

    // A temp file that cannot be removed must not turn a finished save into a failure.
    private static void _Delete(string temp)
    {
        try
        {
            File.Delete(temp);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
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
        // A root that became a link or another form, or lies in a denied folder, is not the folder the operator added.
        if (!PathBoundary.IsPlainRoot(root) || !PathBoundary.Canonical(PathBoundary.RealPath(root)).Equals(root, PathBoundary.Comparison)
            || denied.Any(folder => PathBoundary.IsWithin(folder, root)))
        {
            throw PathBoundary.Outside();
        }
        return PathBoundary.Resolve(root, file.Id, denied);
    }

    // ponytail: Windows only; on Linux a folder swapped for a link between resolve and open, and hard links, stay documented.
    private void _Opened(FileStream stream, bool reading)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        // What was opened, not what was checked, must lie inside the root: a folder can turn into a junction in between.
        if (!PathBoundary.Allowed(root, WindowsFiles.FinalPath(stream.SafeFileHandle), denied))
        {
            throw PathBoundary.Outside();
        }
        // A second name for the same bytes can live anywhere on the volume, so reading through it is refused.
        if (reading && WindowsFiles.Links(stream.SafeFileHandle) > 1)
        {
            throw new FileSourceException(FileSourceErrorKind.Forbidden, "Forbidden: this file is a hard link, so its bytes may also live outside the folder you added.");
        }
    }

    private FileEntry _Entry(string parentId, FileSystemInfo item)
    {
        var id = _Id(parentId, item.Name);
        return item.Attributes.HasFlag(FileAttributes.Directory)
            ? new FileEntry(id, item.Name, id, FileEntryKind.Folder, Modified: item.LastWriteTimeUtc)
            : new FileEntry(id, item.Name, id, FileEntryKind.File, ((FileInfo)item).Length, item.LastWriteTimeUtc,
                _Version(item.LastWriteTimeUtc, ((FileInfo)item).Length));
    }

    private FileEntry? _TryEntry(string parentId, FileSystemInfo item)
    {
        try
        {
            return _Entry(parentId, item);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
