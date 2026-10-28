namespace Cockpit.Plugins.Abstractions.Files;

public sealed record FileRef(string ConnectionId, string Id, string Path);
public sealed record FileConnection(string Id, string Label, string Kind, bool ReadOnly);
public sealed record FileSourceCapabilities(bool CanWrite, long MaxWriteBytes);
public sealed record FileReadResult(Stream Content, string Version, long? Size = null,
    DateTimeOffset? Modified = null, string? ContentType = null, bool CanWrite = false);
public enum FileWriteStatus { Written, Conflict }
public sealed record FileWriteResult(FileWriteStatus Status, string? Version);
public enum FileSourceErrorKind { SignIn, NotFound, Forbidden, TooLarge, Unavailable, Unsupported }
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
}

public interface IFileSourceProvider
{
    IReadOnlyList<FileConnection> Connections { get; }
    event EventHandler? ConnectionsChanged;
    IFileSource Open(string connectionId);
}
