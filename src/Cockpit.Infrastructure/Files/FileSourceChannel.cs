using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Files;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wire = Cockpit.Plugins.Abstractions.Files.FileSourceChannel;

namespace Cockpit.Infrastructure.Files;

internal static class FileSourceChannel
{
    public static IDisposable Register(IPluginBackendChannel channel, IFileSourceProvider provider, ILogger? logger = null)
    {
        var registration = new Registration(provider, logger ?? NullLogger.Instance);
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
                {
                    throw new FileSourceException(FileSourceErrorKind.Unavailable, "The source did not return a file version.");
                }
                var bytes = await Wire.ReadContentAsync(content, Wire.MaxContentBytes, ct).ConfigureAwait(false);
                return new FileSourceReadAnswer(Convert.ToBase64String(bytes), result.Version,
                    bytes.LongLength, result.Modified, result.ContentType)
                {
                    ModifiedBy = result.ModifiedBy,
                    CanWrite = result.CanWrite && source.Capabilities.CanWrite
                        && provider.Connections.FirstOrDefault(c => c.Id == request.File.ConnectionId) is { ReadOnly: false },
                    Live = source is ILiveFileSource,
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
                {
                    throw new FileSourceException(FileSourceErrorKind.TooLarge, "This file exceeds the source's size limit.");
                }
                using var content = new MemoryStream(bytes, writable: false);
                var result = await source.WriteAsync(request.File, content, request.IfVersion,
                    request.MustNotExist, ct).ConfigureAwait(false);
                if (result.Status == FileWriteStatus.Written && string.IsNullOrWhiteSpace(result.Version))
                {
                    throw new FileSourceException(FileSourceErrorKind.Unavailable, "The source did not confirm the saved version.");
                }
                if (result.Status == FileWriteStatus.Written)
                {
                    channel.Publish(Wire.Saved, JsonSerializer.SerializeToElement(request.File, Wire.Json));
                }
                return result;
            })));
            _RegisterLive(channel, provider, registration);
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

    private static void _RegisterLive(IPluginBackendChannel channel, IFileSourceProvider provider, Registration registration)
    {
        registration.Handlers.Add(channel.Handle(Wire.LiveOpen, (payload, ct) => _RunAsync(async () =>
        {
            var request = payload.Deserialize<FileSourceReadRequest>(Wire.Json) ?? throw new JsonException();
            if (_Source(provider, request.File, false) is not ILiveFileSource live)
            {
                throw new FileSourceException(FileSourceErrorKind.Unsupported, "This file is not shared live.");
            }
            var id = Guid.NewGuid().ToString("n");
            var session = await live.OpenLiveAsync(request.File, ct).ConfigureAwait(false);
            var open = new LiveSession(session,
                (_, patch) => channel.Publish(Wire.LivePatch, JsonSerializer.SerializeToElement(
                    new FileSourceLivePatch(id, patch.Rev, [.. patch.Ops], patch.Who), Wire.Json)),
                (_, status) => channel.Publish(Wire.LiveState, JsonSerializer.SerializeToElement(
                    new FileSourceLiveState(id, status), Wire.Json)),
                (_, peers) => channel.Publish(Wire.LivePresence, JsonSerializer.SerializeToElement(
                    new FileSourceLivePresence(id, peers), Wire.Json)));
            registration.Sessions[id] = open;
            try
            {
                return await _SnapshotAsync(id, session, ct).ConfigureAwait(false);
            }
            catch
            {
                registration.Sessions.TryRemove(id, out _);
                await open.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        })));
        registration.Handlers.Add(channel.Handle(Wire.LiveRead, (payload, ct) => _RunAsync(() =>
        {
            var request = payload.Deserialize<FileSourceLiveRequest>(Wire.Json) ?? throw new JsonException();
            return _SnapshotAsync(request.SessionId, _Live(registration, request.SessionId), ct);
        })));
        registration.Handlers.Add(channel.Handle(Wire.LiveEdit, (payload, ct) => _RunAsync(() =>
        {
            var request = payload.Deserialize<FileSourceLiveEditRequest>(Wire.Json) ?? throw new JsonException();
            var ops = request.Ops ?? [];
            var valid = request.Ops is not null && ops.All(op => op?.Text is not null && op.Offset >= 0 && op.Length >= 0)
                && ops.Sum(op => (long)op.Text.Length) <= Wire.MaxContentBytes;
            if (!valid || string.IsNullOrWhiteSpace(request.EditId))
            {
                throw new FileSourceException(FileSourceErrorKind.Unsupported, "The edit is invalid.");
            }
            return _Live(registration, request.SessionId).EditAsync(request.EditId, request.BaseRev, ops, ct);
        })));
        registration.Handlers.Add(channel.Handle(Wire.LiveUndo, (payload, ct) => _RunAsync(async () =>
        {
            var request = payload.Deserialize<FileSourceLiveUndoRequest>(Wire.Json) ?? throw new JsonException();
            await _Live(registration, request.SessionId).UndoAsync(request.Redo, ct).ConfigureAwait(false);
            return request;
        })));
        registration.Handlers.Add(channel.Handle(Wire.LivePresenceSet, (payload, ct) => _RunAsync(async () =>
        {
            var request = payload.Deserialize<FileSourceLivePresenceSet>(Wire.Json) ?? throw new JsonException();
            if (request.State?.Activity is null)
            {
                throw new FileSourceException(FileSourceErrorKind.Unsupported, "The presence is invalid.");
            }
            if (_Live(registration, request.SessionId) is ILivePresence presence)
            {
                await presence.SetPresenceAsync(request.State, ct).ConfigureAwait(false);
            }
            return request;
        })));
        registration.Handlers.Add(channel.Handle(Wire.LiveClose, (payload, _) => _RunAsync(async () =>
        {
            var request = payload.Deserialize<FileSourceLiveRequest>(Wire.Json) ?? throw new JsonException();
            if (registration.Sessions.TryRemove(request.SessionId, out var session))
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            return request;
        })));
    }

    private static async Task<FileSourceLiveSnapshot> _SnapshotAsync(string id, ILiveFileSession session, CancellationToken ct)
    {
        var snapshot = await session.ReadAsync(ct).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(snapshot.Text);
        if (bytes.Length > Wire.MaxContentBytes)
        {
            throw new FileSourceException(FileSourceErrorKind.TooLarge, "This file is too large to edit in Cockpit.");
        }
        return new FileSourceLiveSnapshot(id, Convert.ToBase64String(bytes), snapshot.Rev, snapshot.Status)
        {
            Peers = (session as ILivePresence)?.Peers,
            OpenUrl = (session as ILivePresence)?.OpenUrl,
        };
    }

    private static ILiveFileSession _Live(Registration registration, string? id) =>
        id is not null && registration.Sessions.TryGetValue(id, out var open) ? open.Session
            : throw new FileSourceException(FileSourceErrorKind.NotFound, "This live session has ended.");

    private static IFileSource _Source(IFileSourceProvider provider, FileRef file, bool write)
    {
        if (file is null || string.IsNullOrWhiteSpace(file.ConnectionId)
            || string.IsNullOrWhiteSpace(file.Id) || string.IsNullOrWhiteSpace(file.Path))
        {
            throw new FileSourceException(FileSourceErrorKind.Unsupported, "The file reference is incomplete.");
        }
        var connection = provider.Connections.FirstOrDefault(c => c.Id == file.ConnectionId)
            ?? throw new FileSourceException(FileSourceErrorKind.NotFound, "This file connection is no longer configured.");
        if (write && connection.ReadOnly)
        {
            throw new FileSourceException(FileSourceErrorKind.Forbidden, "This file connection is read-only.");
        }
        var source = provider.Open(connection.Id);
        if (write && !source.Capabilities.CanWrite)
        {
            throw new FileSourceException(FileSourceErrorKind.Forbidden, "This source does not allow writing.");
        }
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

    private sealed class Registration(IFileSourceProvider provider, ILogger logger) : IDisposable
    {
        public List<IDisposable> Handlers { get; } = [];
        public EventHandler? Changed { get; set; }
        public ConcurrentDictionary<string, LiveSession> Sessions { get; } = new(StringComparer.Ordinal);
        public void Dispose()
        {
            provider.ConnectionsChanged -= Changed;
            foreach (var handler in Handlers)
            {
                handler.Dispose();
            }
            Handlers.Clear();
            foreach (var id in Sessions.Keys.ToList())
            {
                if (Sessions.TryRemove(id, out var session))
                {
                    // Unloading cannot await; the close runs on and its failure is logged.
                    _ = _CloseAsync(session);
                }
            }
        }

        private async Task _CloseAsync(LiveSession session)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Closing a live file session failed while its file source was removed.");
            }
        }
    }

    // A session the UI opened; it is closed by files-live-close or when the plugin's file source goes away.
    private sealed class LiveSession : IAsyncDisposable
    {
        private readonly EventHandler<LiveFilePatch> _patched;
        private readonly EventHandler<LiveFileStatus> _status;
        private readonly EventHandler<IReadOnlyList<LivePeer>> _peers;

        public LiveSession(ILiveFileSession session, EventHandler<LiveFilePatch> patched, EventHandler<LiveFileStatus> status,
            EventHandler<IReadOnlyList<LivePeer>> peers)
        {
            Session = session;
            _patched = patched;
            _status = status;
            _peers = peers;
            session.Patched += patched;
            session.StatusChanged += status;
            if (session is ILivePresence presence)
            {
                presence.PeersChanged += peers;
            }
        }

        public ILiveFileSession Session { get; }

        public ValueTask DisposeAsync()
        {
            Session.Patched -= _patched;
            Session.StatusChanged -= _status;
            if (Session is ILivePresence presence)
            {
                presence.PeersChanged -= _peers;
            }
            return Session.DisposeAsync();
        }
    }
}
