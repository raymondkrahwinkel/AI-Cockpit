using System.Text;
using System.Text.Json;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Files;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Core.Abstractions.Remote;

namespace Cockpit.App.Files;

// A client is tied to the plugin/backend channel it was opened on, including a server-admin relay.
internal sealed class FileSourceChannelClient(IPluginUiChannel channel)
{
    public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler) => channel.Subscribe(name, handler);

    public async Task<byte[]> ReadImageAsync(FileRef file, string target, CancellationToken cancellationToken)
    {
        var encoded = await _CallAsync<string>(FileImageChannel.Read, new FileImageRequest(file, target), cancellationToken).ConfigureAwait(false);
        if (encoded.Length > ((FileImageChannel.MaxBytes + 2) / 3) * 4)
        {
            throw new FileSourceException(FileSourceErrorKind.TooLarge, "This image is too large.");
        }
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length > FileImageChannel.MaxBytes)
        {
            throw new FileSourceException(FileSourceErrorKind.TooLarge, "This image is too large.");
        }
        return bytes;
    }

    public async Task<IReadOnlyList<FileConnection>> ConnectionsAsync(CancellationToken cancellationToken) =>
        await _RunAsync(() => _CallAsync<FileConnection[]>(FileSourceChannel.Connections, new { }, cancellationToken), cancellationToken).ConfigureAwait(false);

    public Task<FileListing> ListAsync(FileRef folder, string? pageToken, CancellationToken cancellationToken) =>
        _RunAsync(async () =>
        {
            var listing = await _CallAsync<FileListing>(FileSourceChannel.List, new FileSourceListRequest(folder, pageToken), cancellationToken).ConfigureAwait(false);
            return listing.Entries is null ? throw new FileSourceException(FileSourceErrorKind.Unavailable, "The source returned an invalid folder.") : listing;
        }, cancellationToken);

    public Task<FileEntry> CreateFolderAsync(FileRef parent, string name, CancellationToken cancellationToken) =>
        _RunAsync(() => _CallAsync<FileEntry>(FileSourceChannel.CreateFolder, new FileSourceCreateFolderRequest(parent, name), cancellationToken), cancellationToken);

    public Task<FileEntry> CreateFileAsync(FileRef parent, string name, byte[] content, CancellationToken cancellationToken) =>
        _RunAsync(() => _CallAsync<FileEntry>(FileSourceChannel.CreateFile,
            new FileSourceCreateFileRequest(parent, name, Convert.ToBase64String(content)), cancellationToken), cancellationToken);

    // Size is the whole file's, so the caller knows when it has every block.
    public Task<(byte[] Bytes, string Version, long Size)> ReadRangeAsync(FileRef file, long offset, int count, CancellationToken cancellationToken) =>
        _RunAsync(async () =>
        {
            var block = await _CallAsync<FileSourceReadAnswer>(FileSourceChannel.ReadRange,
                new FileSourceReadRangeRequest(file, offset, count), cancellationToken).ConfigureAwait(false);
            var bytes = FileSourceChannel.DecodeContent(block.ContentBase64 ?? throw new FileSourceException(FileSourceErrorKind.Unavailable, "The source returned an invalid block."));
            return string.IsNullOrWhiteSpace(block.Version) || block.Size is not { } size || bytes.Length > count
                ? throw new FileSourceException(FileSourceErrorKind.Unavailable, "The source returned an invalid block.")
                : (bytes, block.Version, size);
        }, cancellationToken);

    public async Task<FileReadResult> ReadAsync(FileRef file, CancellationToken cancellationToken) =>
        (await ReadWithLiveAsync(file, cancellationToken).ConfigureAwait(false)).Read;

    // Live is the backend's word that the source implements ILiveFileSource.
    public Task<(FileReadResult Read, bool Live)> ReadWithLiveAsync(FileRef file, CancellationToken cancellationToken) => _RunAsync(async () =>
    {
        var read = await _CallAsync<FileSourceReadAnswer>(FileSourceChannel.Read,
            new FileSourceReadRequest(file), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(read.Version) || read.ContentBase64 is null)
        {
            throw new FileSourceException(FileSourceErrorKind.Unavailable, "The source did not return a file version.");
        }
        return (new FileReadResult(new MemoryStream(FileSourceChannel.DecodeContent(read.ContentBase64), writable: false),
            read.Version, read.Size, read.Modified, read.ContentType) { CanWrite = read.CanWrite, ModifiedBy = read.ModifiedBy }, read.Live);
    }, cancellationToken);

    // The wire snapshot comes along for its presence: Peers is non-null when the source shows who else is in the file.
    public Task<(string SessionId, LiveFileSnapshot Snapshot, FileSourceLiveSnapshot Wire)> OpenLiveAsync(FileRef file, CancellationToken cancellationToken) =>
        _RunAsync(async () =>
        {
            var wire = await _CallAsync<FileSourceLiveSnapshot>(FileSourceChannel.LiveOpen,
                new FileSourceReadRequest(file), cancellationToken).ConfigureAwait(false);
            var (id, snapshot) = _Decode(wire);
            return (id, snapshot, wire);
        }, cancellationToken);

    public Task<(string SessionId, LiveFileSnapshot Snapshot)> ReadLiveAsync(string sessionId, CancellationToken cancellationToken) =>
        _RunAsync(async () => _Decode(await _CallAsync<FileSourceLiveSnapshot>(FileSourceChannel.LiveRead,
            new FileSourceLiveRequest(sessionId), cancellationToken).ConfigureAwait(false)), cancellationToken);

    public Task<LiveEditResult> EditLiveAsync(string sessionId, string editId, long baseRev, IReadOnlyList<TextOp> ops, CancellationToken cancellationToken) =>
        _RunAsync(() => _CallAsync<LiveEditResult>(FileSourceChannel.LiveEdit,
            new FileSourceLiveEditRequest(sessionId, editId, baseRev, [.. ops]), cancellationToken), cancellationToken);

    public Task UndoLiveAsync(string sessionId, bool redo, CancellationToken cancellationToken) =>
        _RunAsync(() => _CallAsync<FileSourceLiveUndoRequest>(FileSourceChannel.LiveUndo,
            new FileSourceLiveUndoRequest(sessionId, redo), cancellationToken), cancellationToken);

    public Task ReconnectLiveAsync(string sessionId, bool signIn, CancellationToken cancellationToken) =>
        _RunAsync(() => _CallAsync<FileSourceLiveReconnectRequest>(FileSourceChannel.LiveReconnect,
            new FileSourceLiveReconnectRequest(sessionId, signIn), cancellationToken), cancellationToken);

    public Task SetPresenceAsync(string sessionId, LivePresenceState state, CancellationToken cancellationToken) =>
        _RunAsync(() => _CallAsync<FileSourceLivePresenceSet>(FileSourceChannel.LivePresenceSet,
            new FileSourceLivePresenceSet(sessionId, state), cancellationToken), cancellationToken);

    public Task CloseLiveAsync(string sessionId, CancellationToken cancellationToken) =>
        _RunAsync(() => _CallAsync<FileSourceLiveRequest>(FileSourceChannel.LiveClose,
            new FileSourceLiveRequest(sessionId), cancellationToken), cancellationToken);

    private static (string, LiveFileSnapshot) _Decode(FileSourceLiveSnapshot live) =>
        string.IsNullOrWhiteSpace(live.SessionId) || live.ContentBase64 is null || live.Status is null
            ? throw new FileSourceException(FileSourceErrorKind.Unavailable, "The source returned an invalid live file.")
            : (live.SessionId, new LiveFileSnapshot(new UTF8Encoding(false, true).GetString(FileSourceChannel.DecodeContent(live.ContentBase64)),
                live.Rev, live.Status) { SerializedText = live.SerializedText, PersistedVersion = live.PersistedVersion });

    public Task<FileWriteResult> WriteAsync(FileRef file, Stream content, string? ifVersion,
        bool mustNotExist, CancellationToken cancellationToken) => _RunAsync(async () =>
    {
        FileSourceChannel.ValidateWrite(ifVersion, mustNotExist);
        var bytes = await FileSourceChannel.ReadContentAsync(content, FileSourceChannel.MaxContentBytes, cancellationToken).ConfigureAwait(false);
        var result = await _CallAsync<FileWriteResult>(FileSourceChannel.Write,
            new FileSourceWriteRequest(file, Convert.ToBase64String(bytes), ifVersion, mustNotExist), cancellationToken).ConfigureAwait(false);
        if (result.Status == FileWriteStatus.Written && string.IsNullOrWhiteSpace(result.Version))
        {
            throw new FileSourceException(FileSourceErrorKind.Unavailable, "The source did not confirm the saved version.");
        }
        return result;
    }, cancellationToken);

    private static async Task<T> _RunAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await action().ConfigureAwait(false);
        }
        catch (FileSourceException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or HttpRequestException or JsonException
            or FormatException or InvalidOperationException or ArgumentException or OperationCanceledException or TimeoutException or IRemoteCallFailure)
        {
            throw new FileSourceException(FileSourceErrorKind.Unavailable, "The file source could not complete this operation.");
        }
    }

    private async Task<T> _CallAsync<T>(string action, object request, CancellationToken cancellationToken) where T : class
    {
        var json = await channel.InvokeAsync(action, JsonSerializer.SerializeToElement(request, FileSourceChannel.Json), cancellationToken).ConfigureAwait(false);
        var answer = json.Deserialize<FileSourceAnswer<T>>(FileSourceChannel.Json);
        if (answer?.Error is { } error)
        {
            throw new FileSourceException(error.Kind, error.Message) { Trusted = error.Trusted, Presented = error.Presented };
        }
        return answer?.Data ?? throw new FileSourceException(FileSourceErrorKind.Unavailable, "The file source returned an invalid answer.");
    }
}
