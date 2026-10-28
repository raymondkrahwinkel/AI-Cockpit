using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Files;
using System.Text.Json;
using Wire = Cockpit.Plugins.Abstractions.Files.FileSourceChannel;

namespace Cockpit.Infrastructure.Files;

internal static class FileSourceChannel
{
    public static IDisposable Register(IPluginBackendChannel channel, IFileSourceProvider provider)
    {
        var registration = new Registration(provider);
        try
        {
            registration.Handlers.Add(channel.Handle(Wire.Connections, (_, _) =>
                _RunAsync(() => Task.FromResult(provider.Connections.Select(connection => connection with
                {
                    ReadOnly = connection.ReadOnly || !provider.Open(connection.Id).Capabilities.CanWrite,
                }).ToArray()))));
            registration.Handlers.Add(channel.Handle(Wire.Read, (payload, ct) => _RunAsync(async () =>
            {
                var request = payload.Deserialize<FileSourceReadRequest>(Wire.Json)
                    ?? throw new JsonException();
                var source = _Source(provider, request.File, false);
                var result = await source.ReadAsync(request.File, ct).ConfigureAwait(false);
                await using var content = result.Content;
                if (string.IsNullOrWhiteSpace(result.Version))
                    throw new FileSourceException(FileSourceErrorKind.Unavailable, "The source did not return a file version.");
                var bytes = await Wire.ReadContentAsync(content, Wire.MaxContentBytes, ct).ConfigureAwait(false);
                return new FileSourceReadAnswer(Convert.ToBase64String(bytes), result.Version,
                    bytes.LongLength, result.Modified, result.ContentType)
                {
                    ModifiedBy = result.ModifiedBy,
                    CanWrite = result.CanWrite && source.Capabilities.CanWrite
                        && provider.Connections.FirstOrDefault(c => c.Id == request.File.ConnectionId) is { ReadOnly: false },
                };
            })));
            registration.Handlers.Add(channel.Handle(Wire.Write, (payload, ct) => _RunAsync(async () =>
            {
                var request = payload.Deserialize<FileSourceWriteRequest>(Wire.Json)
                    ?? throw new JsonException();
                Wire.ValidateWrite(request.IfVersion, request.MustNotExist);
                var source = _Source(provider, request.File, true);
                var bytes = Wire.DecodeContent(request.ContentBase64 ?? throw new JsonException());
                if (bytes.LongLength > source.Capabilities.MaxWriteBytes)
                    throw new FileSourceException(FileSourceErrorKind.TooLarge, "This file exceeds the source's size limit.");
                using var content = new MemoryStream(bytes, writable: false);
                var result = await source.WriteAsync(request.File, content, request.IfVersion,
                    request.MustNotExist, ct).ConfigureAwait(false);
                if (result.Status == FileWriteStatus.Written && string.IsNullOrWhiteSpace(result.Version))
                    throw new FileSourceException(FileSourceErrorKind.Unavailable, "The source did not confirm the saved version.");
                if (result.Status == FileWriteStatus.Written)
                {
                    channel.Publish(Wire.Saved, JsonSerializer.SerializeToElement(request.File, Wire.Json));
                }
                return result;
            })));
            registration.Changed = (_, _) => channel.Publish(Wire.Changed,
                JsonSerializer.SerializeToElement(provider.Connections, Wire.Json));
            provider.ConnectionsChanged += registration.Changed;
            return registration;
        }
        catch
        {
            registration.Dispose();
            throw;
        }
    }

    private static IFileSource _Source(IFileSourceProvider provider, FileRef file, bool write)
    {
        if (file is null || string.IsNullOrWhiteSpace(file.ConnectionId)
            || string.IsNullOrWhiteSpace(file.Id) || string.IsNullOrWhiteSpace(file.Path))
            throw new FileSourceException(FileSourceErrorKind.Unsupported, "The file reference is incomplete.");
        var connection = provider.Connections.FirstOrDefault(c => c.Id == file.ConnectionId)
            ?? throw new FileSourceException(FileSourceErrorKind.NotFound, "This file connection is no longer configured.");
        if (write && connection.ReadOnly)
            throw new FileSourceException(FileSourceErrorKind.Forbidden, "This file connection is read-only.");
        var source = provider.Open(connection.Id);
        if (write && !source.Capabilities.CanWrite)
            throw new FileSourceException(FileSourceErrorKind.Forbidden, "This source does not allow writing.");
        return source;
    }

    private static async Task<JsonElement> _RunAsync<T>(Func<Task<T>> action) where T : class
    {
        try
        {
            return JsonSerializer.SerializeToElement(new FileSourceAnswer<T>(await action().ConfigureAwait(false), null), Wire.Json);
        }
        catch (OperationCanceledException) { throw; }
        catch (FileSourceException error)
        {
            return JsonSerializer.SerializeToElement(new FileSourceAnswer<T>(null,
                new FileSourceError(error.Kind, error.Message)), Wire.Json);
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException)
        {
            return JsonSerializer.SerializeToElement(new FileSourceAnswer<T>(null,
                new FileSourceError(FileSourceErrorKind.Unsupported, "The file request is invalid.")), Wire.Json);
        }
        catch (Exception)
        {
            return JsonSerializer.SerializeToElement(new FileSourceAnswer<T>(null,
                new FileSourceError(FileSourceErrorKind.Unavailable, "The file source could not complete this operation.")), Wire.Json);
        }
    }

    private sealed class Registration(IFileSourceProvider provider) : IDisposable
    {
        public List<IDisposable> Handlers { get; } = [];
        public EventHandler? Changed { get; set; }
        public void Dispose()
        {
            provider.ConnectionsChanged -= Changed;
            foreach (var handler in Handlers) handler.Dispose();
            Handlers.Clear();
        }
    }
}
