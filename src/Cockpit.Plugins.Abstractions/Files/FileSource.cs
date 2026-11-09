namespace Cockpit.Plugins.Abstractions.Files;

public sealed record FileRef(string ConnectionId, string Id, string Path)
{
    /// <summary>The top folder a browser lists first; the source maps "/" to its own root. SDK 3.79.0.</summary>
    public static FileRef Root(string connectionId) => new(connectionId, "/", "/");
}
public sealed record FileConnection(string Id, string Label, string Kind, bool ReadOnly)
{
    /// <summary>Set by the host from the source's capabilities, so a browser hides what the backend would refuse. SDK 3.79.0.</summary>
    public bool CanCreateFolder { get; init; }
    /// <summary>As <see cref="CanCreateFolder"/>, for new files and uploads. SDK 3.79.0.</summary>
    public bool CanCreateFile { get; init; }
}
public sealed record FileSourceCapabilities(bool CanWrite, long MaxWriteBytes)
{
    /// <summary>The source implements <see cref="IFileSource.CreateFolderAsync"/>. SDK 3.79.0.</summary>
    public bool CanCreateFolder { get; init; }
    /// <summary>The source implements <see cref="IFileSource.CreateFileAsync"/>. SDK 3.79.0.</summary>
    public bool CanCreateFile { get; init; }
}
public enum FileEntryKind { Folder, File }
/// <summary>One item in a folder. Id is the source's own; Path is for display, '/'-separated from the connection's root.</summary>
public sealed record FileEntry(string Id, string Name, string Path, FileEntryKind Kind, long? Size = null,
    DateTimeOffset? Modified = null, string? Version = null);
/// <summary>One page of a folder; a non-null NextPageToken asks for the next page.</summary>
public sealed record FileListing(IReadOnlyList<FileEntry> Entries, string? NextPageToken = null);
public sealed record FileReadResult(Stream Content, string Version, long? Size = null,
    DateTimeOffset? Modified = null, string? ContentType = null)
{
    public bool CanWrite { get; init; }
    public string? ModifiedBy { get; init; }
}
public enum FileWriteStatus { Written, Conflict }
public sealed record FileWriteResult(FileWriteStatus Status, string? Version);
public enum FileSourceErrorKind { SignIn, NotFound, Forbidden, TooLarge, Unavailable, Unsupported, Conflict }
public sealed class FileSourceException(FileSourceErrorKind kind, string message) : Exception(message)
{
    public FileSourceErrorKind Kind { get; } = kind;
}

public interface IFileSource
{
    FileSourceCapabilities Capabilities { get; }

    /// <summary>Returns content and version from the same snapshot. The caller disposes Content.</summary>
    Task<FileReadResult> ReadAsync(FileRef file, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically saves against ifVersion, or creates with mustNotExist. Exactly one condition is required.
    /// A conflict writes nothing; Written returns the version of the committed bytes. The caller owns content.
    /// </summary>
    Task<FileWriteResult> WriteAsync(FileRef target, Stream content, string? ifVersion,
        bool mustNotExist, CancellationToken cancellationToken);

    /// <summary>
    /// Lists one folder, one page at a time; <see cref="FileRef.Root"/> is the top. SDK 3.79.0; unsupported by default.
    /// </summary>
    Task<FileListing> ListAsync(FileRef folder, string? pageToken, CancellationToken cancellationToken) =>
        Task.FromException<FileListing>(Unsupported("Browsing"));

    /// <summary>Creates a folder in <paramref name="parent"/>; only when Capabilities.CanCreateFolder. SDK 3.79.0.</summary>
    Task<FileEntry> CreateFolderAsync(FileRef parent, string name, CancellationToken cancellationToken) =>
        Task.FromException<FileEntry>(Unsupported("Creating folders"));

    /// <summary>
    /// Creates <paramref name="name"/> in <paramref name="parent"/> with <paramref name="content"/>, only when Capabilities.CanCreateFile;
    /// the source names the new item, so the host never builds a path. An existing name throws Conflict. SDK 3.79.0.
    /// </summary>
    Task<FileEntry> CreateFileAsync(FileRef parent, string name, Stream content, CancellationToken cancellationToken) =>
        Task.FromException<FileEntry>(Unsupported("Creating files"));

    /// <summary>
    /// Reads at most <paramref name="count"/> bytes (at most 1 MiB) from <paramref name="offset"/>, with the version and
    /// the whole file's Size from the same snapshot. The caller disposes Content. SDK 3.79.0; unsupported by default.
    /// </summary>
    Task<FileReadResult> ReadRangeAsync(FileRef file, long offset, int count, CancellationToken cancellationToken) =>
        Task.FromException<FileReadResult>(Unsupported("Reading in blocks"));

    private static FileSourceException Unsupported(string what) =>
        new(FileSourceErrorKind.Unsupported, $"{what} is not supported by this source.");
}

public interface IFileSourceProvider
{
    IReadOnlyList<FileConnection> Connections { get; }
    event EventHandler? ConnectionsChanged;
    IFileSource Open(string connectionId);
}
