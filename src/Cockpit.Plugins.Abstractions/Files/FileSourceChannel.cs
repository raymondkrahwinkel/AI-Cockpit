using System.Text.Json;

namespace Cockpit.Plugins.Abstractions.Files;

public static class FileSourceChannel
{
    public const int MaxContentBytes = 1024 * 1024;
    public const string Connections = "files-connections";
    public const string Read = "files-read";
    public const string Write = "files-write";
    public const string Changed = "files-changed";
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);
}

public sealed record FileSourceReadRequest(FileRef File);
public sealed record FileSourceWriteRequest(FileRef File, string ContentBase64, string? IfVersion, bool MustNotExist);
public sealed record FileSourceReadAnswer(string ContentBase64, string Version, long? Size,
    DateTimeOffset? Modified, string? ContentType);
public sealed record FileSourceError(FileSourceErrorKind Kind, string Message);
public sealed record FileSourceAnswer<T>(T? Data, FileSourceError? Error) where T : class;
