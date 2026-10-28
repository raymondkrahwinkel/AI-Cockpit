using System.Text.Json;
using Cockpit.Plugins.Abstractions.Files;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.App.Files;

// A client is tied to the plugin/backend channel it was opened on, including a server-admin relay.
internal sealed class FileSourceChannelClient(IPluginUiChannel channel)
{
    public async Task<IReadOnlyList<FileConnection>> ConnectionsAsync(CancellationToken cancellationToken) =>
        await _CallAsync<FileConnection[]>(FileSourceChannel.Connections, new { }, cancellationToken).ConfigureAwait(false);

    public async Task<FileReadResult> ReadAsync(FileRef file, CancellationToken cancellationToken)
    {
        var read = await _CallAsync<FileSourceReadAnswer>(FileSourceChannel.Read,
            new FileSourceReadRequest(file), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(read.Version))
            throw new FileSourceException(FileSourceErrorKind.Unavailable, "The source did not return a file version.");
        return new FileReadResult(new MemoryStream(FileSourceChannel.DecodeContent(read.ContentBase64), writable: false),
            read.Version, read.Size, read.Modified, read.ContentType);
    }

    public async Task<FileWriteResult> WriteAsync(FileRef file, Stream content, string? ifVersion,
        bool mustNotExist, CancellationToken cancellationToken)
    {
        FileSourceChannel.ValidateWrite(ifVersion, mustNotExist);
        var bytes = await FileSourceChannel.ReadContentAsync(content, FileSourceChannel.MaxContentBytes, cancellationToken).ConfigureAwait(false);
        return await _CallAsync<FileWriteResult>(FileSourceChannel.Write,
            new FileSourceWriteRequest(file, Convert.ToBase64String(bytes), ifVersion, mustNotExist), cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> _CallAsync<T>(string action, object request, CancellationToken cancellationToken) where T : class
    {
        var json = await channel.InvokeAsync(action, JsonSerializer.SerializeToElement(request, FileSourceChannel.Json), cancellationToken).ConfigureAwait(false);
        var answer = json.Deserialize<FileSourceAnswer<T>>(FileSourceChannel.Json);
        if (answer?.Error is { } error) throw new FileSourceException(error.Kind, error.Message);
        return answer?.Data ?? throw new FileSourceException(FileSourceErrorKind.Unavailable, "The file source returned an invalid answer.");
    }
}
