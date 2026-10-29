using System.Text.Json;

namespace Cockpit.Plugins.Abstractions.Files;

public static class FileSourceChannel
{
    public const int MaxContentBytes = 1024 * 1024;
    public const string Connections = "files-connections";
    public const string Read = "files-read";
    public const string Write = "files-write";
    public const string Changed = "files-changed";
    public const string Saved = "files-saved";
    public const string LiveOpen = "files-live-open";
    public const string LiveRead = "files-live-read";
    public const string LiveEdit = "files-live-edit";
    public const string LiveUndo = "files-live-undo";
    public const string LiveClose = "files-live-close";
    public const string LivePatch = "files-live-patch";
    public const string LiveState = "files-live-state";
    public const string LivePresence = "files-live-presence";
    public const string LivePresenceSet = "files-live-presence-set";
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    public static async Task<byte[]> ReadContentAsync(Stream content, long limit, CancellationToken cancellationToken)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > limit)
            {
                throw new FileSourceException(FileSourceErrorKind.TooLarge, "This file is too large to edit in Cockpit.");
            }
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }

    public static void ValidateWrite(string? ifVersion, bool mustNotExist)
    {
        if (mustNotExist ? ifVersion is not null : string.IsNullOrWhiteSpace(ifVersion))
        {
            throw new FileSourceException(FileSourceErrorKind.Unsupported, "A save needs a base version; a new file must not exist.");
        }
    }

    public static byte[] DecodeContent(string content)
    {
        if (content.Length > ((MaxContentBytes + 2) / 3) * 4)
        {
            throw new FileSourceException(FileSourceErrorKind.TooLarge, "This file is too large to edit in Cockpit.");
        }
        var bytes = Convert.FromBase64String(content);
        if (bytes.Length > MaxContentBytes)
        {
            throw new FileSourceException(FileSourceErrorKind.TooLarge, "This file is too large to edit in Cockpit.");
        }
        return bytes;
    }
}

public sealed record FileSourceReadRequest(FileRef File);
public sealed record FileSourceWriteRequest(FileRef File, string ContentBase64, string? IfVersion, bool MustNotExist);
public sealed record FileSourceReadAnswer(string ContentBase64, string Version, long? Size,
    DateTimeOffset? Modified, string? ContentType)
{
    public bool CanWrite { get; init; }
    public string? ModifiedBy { get; init; }
    public bool Live { get; init; }
}
public sealed record FileSourceLiveRequest(string SessionId);
public sealed record FileSourceLiveEditRequest(string SessionId, string EditId, long BaseRev, TextOp[] Ops);
public sealed record FileSourceLiveUndoRequest(string SessionId, bool Redo);
public sealed record FileSourceLiveSnapshot(string SessionId, string ContentBase64, long Rev, LiveFileStatus Status)
{
    public IReadOnlyList<LivePeer>? Peers { get; init; }
    public string? OpenUrl { get; init; }
}
public sealed record FileSourceLivePresenceSet(string SessionId, LivePresenceState State);
public sealed record FileSourceLivePresence(string SessionId, IReadOnlyList<LivePeer> Peers);
public sealed record FileSourceLivePatch(string SessionId, long Rev, TextOp[] Ops, string? Who)
{
    /// <summary>Optional attribution of an outside write.</summary>
    public ExternalTextEdit? ExternalEdit { get; init; }
}
public sealed record FileSourceLiveState(string SessionId, LiveFileStatus Status);
public sealed record FileSourceError(FileSourceErrorKind Kind, string Message);
public sealed record FileSourceAnswer<T>(T? Data, FileSourceError? Error) where T : class;
