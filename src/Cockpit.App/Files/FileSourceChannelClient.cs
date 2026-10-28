using System.Text.Json;
using Cockpit.Plugins.Abstractions.Files;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Core.Abstractions.Remote;

namespace Cockpit.App.Files;

// A client is tied to the plugin/backend channel it was opened on, including a server-admin relay.
internal sealed class FileSourceChannelClient(IPluginUiChannel channel)
{
    public async Task<IReadOnlyList<FileConnection>> ConnectionsAsync(CancellationToken cancellationToken) =>
        await _RunAsync(() => _CallAsync<FileConnection[]>(FileSourceChannel.Connections, new { }, cancellationToken), cancellationToken).ConfigureAwait(false);

    public Task<FileReadResult> ReadAsync(FileRef file, CancellationToken cancellationToken) => _RunAsync(async () =>
    {
        var read = await _CallAsync<FileSourceReadAnswer>(FileSourceChannel.Read,
            new FileSourceReadRequest(file), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(read.Version) || read.ContentBase64 is null)
            throw new FileSourceException(FileSourceErrorKind.Unavailable, "The source did not return a file version.");
        return new FileReadResult(new MemoryStream(FileSourceChannel.DecodeContent(read.ContentBase64), writable: false),
            read.Version, read.Size, read.Modified, read.ContentType) { CanWrite = read.CanWrite, ModifiedBy = read.ModifiedBy };
    }, cancellationToken);

    public Task<FileWriteResult> WriteAsync(FileRef file, Stream content, string? ifVersion,
        bool mustNotExist, CancellationToken cancellationToken) => _RunAsync(async () =>
    {
        FileSourceChannel.ValidateWrite(ifVersion, mustNotExist);
        var bytes = await FileSourceChannel.ReadContentAsync(content, FileSourceChannel.MaxContentBytes, cancellationToken).ConfigureAwait(false);
        var result = await _CallAsync<FileWriteResult>(FileSourceChannel.Write,
            new FileSourceWriteRequest(file, Convert.ToBase64String(bytes), ifVersion, mustNotExist), cancellationToken).ConfigureAwait(false);
        if (result.Status == FileWriteStatus.Written && string.IsNullOrWhiteSpace(result.Version))
            throw new FileSourceException(FileSourceErrorKind.Unavailable, "The source did not confirm the saved version.");
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
        if (answer?.Error is { } error) throw new FileSourceException(error.Kind, error.Message);
        return answer?.Data ?? throw new FileSourceException(FileSourceErrorKind.Unavailable, "The file source returned an invalid answer.");
    }
}
