using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Events;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot.Mobile;

// AC-2224: pairing a phone from a connection's row and keeping the phones paired to it. Depot issues the pairing id and
// carries the frames; whether a phone gets in is decided here, by MobileHandshake, never by what Depot says.
internal sealed class MobilePairing(
    Func<IReadOnlyList<DepotConnectionRegistration>> connections,
    Func<DepotConnectionRegistration, HttpMessageHandler> hostHandler,
    Func<DepotConnectionRegistration, HttpMessageHandler> userHandler,
    IPluginStorage storage,
    DepotEvents events,
    Func<AssistantChannelContribution, IAssistantChannelGateway?> openChannel,
    ILogger? logger = null,
    Func<string, string, Func<byte[]?>, Task>? send = null,
    TimeProvider? time = null,
    ICockpitHost? host = null) : IAsyncDisposable
{
    private static readonly TimeSpan _ConfirmationTimeout = TimeSpan.FromSeconds(15);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly MobileKeyStore _keys = new(storage);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, _Pending> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MobileRelay> _relays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IDisposable> _deviceEvents = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Connection, string Device), _Stream> _streams = [];
    private readonly Dictionary<(string Connection, string Device), MobileAssistantChannel> _channels = [];

    public int NotifyOperatorPhone(Cockpit.Plugins.Abstractions.Notifications.OperatorPhoneNotification notification)
    {
        (string Connection, string Device)[] phones;
        lock (_gate)
        {
            phones = _channels.Keys.ToArray();
        }
        int accepted = 0;
        foreach (var phone in phones)
        {
            byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "notify",
                id = Guid.NewGuid().ToString("n"),
                body = new
                {
                    kind = "targeted",
                    senderId = notification.SenderId,
                    sender = notification.Sender,
                    title = notification.Title,
                    text = notification.Text,
                    urgency = notification.Urgency,
                    target = notification.Target,
                },
            });
            if (_SendToPhone(phone.Connection, phone.Device, plaintext))
            {
                accepted++;
            }
        }
        return accepted;
    }

    // Connects the relay of every connection that has a paired phone, so those phones can reach this Cockpit.
    public void Start()
    {
        foreach (var connection in connections().Where(connection => _Phones(connection.Id).Count > 0))
        {
            lock (_gate)
            {
                foreach (var phone in _Phones(connection.Id))
                {
                    _Channel(connection.Id, phone.DeviceId);
                }
            }

            _ = _EnsureAsync(connection, CancellationToken.None);
        }
    }

    public async Task<DepotPairingStatus> StartAsync(string connectionId, CancellationToken cancellationToken)
    {
        var connection = _Connection(connectionId);
        _Discard(connectionId);
        _Issued? issued;
        try
        {
            using var http = new HttpClient(hostHandler(connection)) { Timeout = TimeSpan.FromSeconds(30) };
            using var response = await http.PostAsync($"{connection.Url.TrimEnd('/')}/cockpit/mobile/pairings", null, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new(DepotPairingState.Unavailable, "The mobile app is not available on this Depot account, or it is turned off.", Link: _AccountLink(connection));
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new(DepotPairingState.Failed, $"Sign in to {connection.Name} first.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new(DepotPairingState.Failed, $"{connection.Name} refused the request (HTTP {(int)response.StatusCode}).");
            }

            issued = await response.Content.ReadFromJsonAsync<_Issued>(DepotChannel.Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger?.LogDebug(exception, "Asking {Connection} for a pairing id failed.", connection.Name);
            return new(DepotPairingState.Failed, $"{connection.Name} could not be reached.");
        }

        if (string.IsNullOrWhiteSpace(issued?.PairingId) || string.IsNullOrWhiteSpace(issued.DepotUrl))
        {
            return new(DepotPairingState.Failed, $"{connection.Name} answered without a pairing id.");
        }

        if (!_keys.TryGetCockpitKey(connectionId, out var key))
        {
            return new(DepotPairingState.Failed, "This Cockpit's phone key cannot be read, so no phone can pair with it.");
        }

        PairingPayload payload;
        using (key)
        {
            payload = PairingPayload.Create(issued.DepotUrl, issued.PairingId, MobileKeyStore.PublicKey(key!));
        }

        lock (_gate)
        {
            _pending[connectionId] = new _Pending(payload);
        }

        await _EnsureAsync(connection, cancellationToken).ConfigureAwait(false);
        logger?.LogInformation("Mobile pairing code issued for connection {ConnectionId}.", connectionId);
        return new(DepotPairingState.Open, Qr: payload.Encode(), ExpiresAt: payload.ExpiresAt);
    }

    public DepotPairingStatus Status(string connectionId)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(connectionId, out var pending))
            {
                return new(DepotPairingState.None);
            }

            if (pending.DeviceName is { } paired && pending.Paired)
            {
                return new(DepotPairingState.Paired, DeviceName: paired);
            }

            if (pending.DeviceId is { } device && _streams.TryGetValue((connectionId, device), out var stream)
                && ReferenceEquals(stream.Pairing, pending))
            {
                return new(DepotPairingState.Waiting);
            }

            if (!pending.Payload.IsOpen())
            {
                return new(DepotPairingState.Expired, "This code has expired. Nothing was paired.");
            }

            return new(pending.DeviceId is null ? DepotPairingState.Open : DepotPairingState.Waiting, ExpiresAt: pending.Payload.ExpiresAt);
        }
    }

    public void Cancel(string connectionId)
    {
        logger?.LogInformation("Mobile pairing dialog closed for connection {ConnectionId}.", connectionId);
        _Discard(connectionId);
        _ = PruneAsync();
    }

    // Depot's device list is grant-scoped and has no registration identity; absence cannot revoke a saved key.
    public async Task<DepotPairedPhones> PhonesAsync(string connectionId, bool refresh, CancellationToken cancellationToken)
    {
        var connection = _Connection(connectionId);
        if (refresh && _Phones(connectionId).Count > 0 && await _DepotDevicesAsync(connection, cancellationToken).ConfigureAwait(false) is { } devices)
        {
            var known = devices.Where(device => device.Id is not null).DistinctBy(device => device.Id).ToDictionary(device => device.Id, StringComparer.Ordinal);
            lock (_gate)
            {
                foreach (var phone in _Phones(connectionId))
                {
                    if (!known.TryGetValue(phone.DeviceId, out var device))
                    {
                        logger?.LogWarning("Depot's device list omitted a saved phone for connection {ConnectionId}; keeping its key. Pair again if the sign-in changed.", connectionId);
                    }
                    else if (!string.IsNullOrWhiteSpace(device.Name) && device.Name != phone.Name)
                    {
                        _Save(connectionId, [.. _Phones(connectionId).Select(candidate => candidate.DeviceId == phone.DeviceId ? candidate with { Name = device.Name } : candidate)]);
                    }
                }
            }
        }

        if (refresh)
        {
            _ = PruneAsync();
        }

        List<MobilePairedPhone> phones = _Phones(connectionId);
        byte[]? cockpitPub = null;
        if (phones.Count > 0 && _keys.TryGetCockpitKey(connectionId, out var key))
        {
            using (key)
            {
                if (key is not null)
                {
                    cockpitPub = MobileKeyStore.PublicKey(key);
                }
            }
        }

        return new([.. phones.Select(phone => new DepotPairedPhone(phone.DeviceId, phone.Name, phone.PairedAt,
            cockpitPub is null ? null : MobileKeyFingerprint.Format(cockpitPub, Base64Url.DecodeFromChars(phone.PhonePub))))], _AccountLink(connection));
    }

    public void Forget(string connectionId, string deviceId)
    {
        lock (_gate)
        {
            _Forget(connectionId, deviceId);
        }

        _ = PruneAsync();
    }

    // Stops the relay of a connection that has nothing left to listen for: no open code and no paired phone, or no
    // connection at all any more, so no reconnect loop outlives what it was for.
    public async Task PruneAsync()
    {
        var live = connections().Select(connection => connection.Id).ToHashSet(StringComparer.Ordinal);
        List<string> idle;
        lock (_gate)
        {
            idle = [.. _relays.Keys.Where(id => !live.Contains(id) || (!_pending.ContainsKey(id) && _Phones(id).Count == 0))];
            foreach (var id in idle)
            {
                _DropStreams(id);
                if (!live.Contains(id))
                {
                    _DropChannels(id);
                }

                if (!live.Contains(id) && _deviceEvents.Remove(id, out var subscription))
                {
                    subscription.Dispose();
                }
            }
        }

        foreach (var id in idle)
        {
            if (!live.Contains(id))
            {
                _Discard(id);
            }

            try
            {
                await _DropRelayAsync(id).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger?.LogDebug(exception, "Stopping an idle mobile relay failed.");
            }
        }
    }

    // A new account on the connection's server: its relay, open code and streams belonged to the previous one.
    public async Task ResetAsync(string serverName)
    {
        foreach (var connection in connections().Where(connection => connection.McpServerName == serverName))
        {
            logger?.LogInformation("Resetting the mobile connection after an account change; {PhoneCount} saved phone(s) retained for {ConnectionId}.",
                _Phones(connection.Id).Count, connection.Id);
            _Discard(connection.Id);
            MobileRelay? relay;
            lock (_gate)
            {
                _relays.Remove(connection.Id, out relay);
                _DropStreams(connection.Id);
                _DropChannels(connection.Id);
            }

            if (relay is not null)
            {
                await relay.DisposeAsync().ConfigureAwait(false);
            }

            if (_Phones(connection.Id).Count > 0)
            {
                await _EnsureAsync(connection, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<MobileRelay> relays;
        lock (_gate)
        {
            foreach (var pending in _pending.Values)
            {
                pending.Payload.Discard();
            }

            _pending.Clear();
            foreach (var subscription in _deviceEvents.Values)
            {
                subscription.Dispose();
            }

            _deviceEvents.Clear();
            foreach (var connection in _streams.Keys.Concat(_channels.Keys).Select(key => key.Connection).Distinct().ToList())
            {
                _DropStreams(connection);
                _DropChannels(connection);
            }

            relays = [.. _relays.Values];
            _relays.Clear();
        }

        foreach (var relay in relays)
        {
            await relay.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task _EnsureAsync(DepotConnectionRegistration connection, CancellationToken cancellationToken)
    {
        bool subscribe;
        lock (_gate)
        {
            subscribe = !_deviceEvents.ContainsKey(connection.Id);
            if (subscribe)
            {
                _deviceEvents[connection.Id] = new _NoSubscription();
            }
        }

        MobileRelay? relay = null;
        try
        {
            if (subscribe)
            {
                var subscription = await events.SubscribeAsync(
                    connection.Id, DepotEventSubscriber.DevicesTopic, cockpitEvent => _OnDeviceEvent(connection.Id, cockpitEvent),
                    () => _ = PhonesAsync(connection.Id, true, CancellationToken.None), cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    _deviceEvents[connection.Id] = subscription;
                }
            }

            lock (_gate)
            {
                if (_relays.ContainsKey(connection.Id))
                {
                    return;
                }
            }

            if (await _UserIdAsync(connection, cancellationToken).ConfigureAwait(false) is not { } userId)
            {
                return;
            }

            relay = new MobileRelay(connection, userId, () => hostHandler(connection),
                (streamId, deviceId) => Opened(connection.Id, streamId, deviceId),
                (streamId, deviceId, frame) => Frame(connection.Id, streamId, deviceId, frame),
                (streamId, deviceId) => Closed(connection.Id, streamId, deviceId));
            lock (_gate)
            {
                if (!_relays.TryAdd(connection.Id, relay))
                {
                    _ = relay.DisposeAsync();
                    return;
                }
            }

            relay.Closed += closed => _OnRelayClosed(connection.Id, closed);
            await relay.StartAsync(cancellationToken).ConfigureAwait(false);
            logger?.LogInformation("Mobile relay connected for connection {ConnectionId}.", connection.Id);
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (_deviceEvents.TryGetValue(connection.Id, out var subscription) && subscription is _NoSubscription)
                {
                    _deviceEvents.Remove(connection.Id);
                }
            }

            if (relay is not null)
            {
                _OnRelayClosed(connection.Id, relay);
                await relay.DisposeAsync().ConfigureAwait(false);
            }
            logger?.LogWarning(exception, "Connecting the mobile relay for {Connection} failed.", connection.Name);
        }
    }

    private void _OnRelayClosed(string connectionId, MobileRelay closed)
    {
        lock (_gate)
        {
            if (_relays.TryGetValue(connectionId, out var current) && ReferenceEquals(current, closed))
            {
                logger?.LogInformation("Mobile relay disconnected for connection {ConnectionId}.", connectionId);
                _relays.Remove(connectionId);
                _DropStreams(connectionId);
            }
        }
    }

    private async Task _DropRelayAsync(string connectionId)
    {
        MobileRelay? relay;
        lock (_gate)
        {
            _relays.Remove(connectionId, out relay);
        }

        if (relay is not null)
        {
            await relay.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void _OnDeviceEvent(string connectionId, DepotEvent cockpitEvent)
    {
        if (cockpitEvent.DeviceId is not { } deviceId)
        {
            return;
        }

        lock (_gate)
        {
            if (cockpitEvent.Kind == "deviceUnpaired")
            {
                logger?.LogInformation("Depot reported a phone unpaired for connection {ConnectionId}.", connectionId);
                _Forget(connectionId, deviceId);
                _ = PruneAsync();
            }
            else if (cockpitEvent.Kind == "devicePaired" && _pending.TryGetValue(connectionId, out var pending) && pending.Payload.IsOpen())
            {
                logger?.LogInformation("Depot reported a phone pairing for the open code on connection {ConnectionId}.", connectionId);
                pending.DeviceId = deviceId;
                pending.DeviceName = string.IsNullOrWhiteSpace(cockpitEvent.Name) ? null : cockpitEvent.Name;
            }
        }
    }

    // A new stream from a phone: its first frame will be the handshake, keyed to this stream id (PROTOCOL.md § 4).
    internal void Opened(string connectionId, string streamId, string deviceId)
    {
        logger?.LogInformation("Mobile relay stream opened for connection {ConnectionId}.", connectionId);
        lock (_gate)
        {
            _DropStream(connectionId, deviceId);
            _streams[(connectionId, deviceId)] = new _Stream(streamId);
        }
    }

    // A close names its stream, so a late close of a replaced stream leaves the current one alone.
    internal void Closed(string connectionId, string streamId, string deviceId)
    {
        logger?.LogInformation("Mobile relay stream closed for connection {ConnectionId}.", connectionId);
        lock (_gate)
        {
            _DropStream(connectionId, deviceId, streamId);
        }
    }

    // Only the device's current stream is read; a frame of any other stream id is refused. A frame reaches the
    // assistant only through the channel of a phone that is still paired, after its stream's keys opened it.
    internal void Frame(string connectionId, string streamId, string deviceId, byte[] frame)
    {
        byte[]? reply = null;
        MobileAssistantChannel? channel = null;
        byte[] plaintext = [];
        lock (_gate)
        {
            if (!_streams.TryGetValue((connectionId, deviceId), out var stream) || stream.StreamId != streamId)
            {
                return;
            }

            if (stream.Cipher is { } cipher)
            {
                // A frame that does not open ends the stream.
                if (!cipher.TryOpen(frame, out plaintext))
                {
                    _DropStream(connectionId, deviceId);
                    return;
                }

                if (!stream.Confirmed)
                {
                    try
                    {
                        if (!stream.ReadySent || stream.ConfirmationStartedAt is not { } started
                            || _time.GetElapsedTime(started) >= _ConfirmationTimeout || !_IsReady(plaintext))
                        {
                            logger?.LogWarning("Mobile key confirmation refused for connection {ConnectionId}.", connectionId);
                            _DropStream(connectionId, deviceId);
                            return;
                        }

                        if (stream.Pairing is { } pending && stream.PhonePub is { } phonePub)
                        {
                            _RemoveKey(connectionId, deviceId);
                            _keys.AddPhone(connectionId, phonePub);
                            var name = pending.DeviceName ?? _ShortName(deviceId);
                            _Save(connectionId, [.. _Phones(connectionId), new MobilePairedPhone(deviceId, name, Base64Url.EncodeToString(phonePub), _time.GetUtcNow())]);
                            pending.DeviceName = name;
                            pending.Paired = true;
                            logger?.LogInformation("Mobile pairing confirmed and phone saved for connection {ConnectionId}.", connectionId);
                        }

                        stream.Confirmed = true;
                        stream.Pairing = null;
                        stream.PhonePub = null;
                        stream.ConfirmationTimer?.Dispose();
                        stream.ConfirmationTimer = null;
                        _Channel(connectionId, deviceId)?.StreamStarted(streamId);
                        return;
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(plaintext);
                    }
                }

                channel = _Channel(connectionId, deviceId);
            }
            else
            {
                reply = _Handshake(connectionId, deviceId, stream, frame);
                if (reply is null)
                {
                    _DropStream(connectionId, deviceId);
                    return;
                }
            }
        }

        if (reply is not null)
        {
            _ = _SendHandshakeAsync(connectionId, deviceId, streamId, reply);
            return;
        }

        try
        {
            channel?.Receive(plaintext, streamId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static bool _IsReady(byte[] plaintext)
    {
        try
        {
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "ready"
                && root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(id.GetString())
                && root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task _SendHandshakeAsync(string connectionId, string deviceId, string streamId, byte[] reply)
    {
        await _SendAsync(connectionId, deviceId, streamId, () =>
        {
            lock (_gate)
            {
                return _streams.TryGetValue((connectionId, deviceId), out var current) && current.StreamId == streamId ? reply : null;
            }
        }).ConfigureAwait(false);

        lock (_gate)
        {
            if (!_streams.TryGetValue((connectionId, deviceId), out var current) || current.StreamId != streamId)
            {
                return;
            }

            current.ConfirmationStartedAt = _time.GetTimestamp();
            logger?.LogInformation("Mobile handshake reply sent for connection {ConnectionId}.", connectionId);
            current.ConfirmationTimer = _time.CreateTimer(_ =>
            {
                lock (_gate)
                {
                    if (!current.Confirmed && ReferenceEquals(_streams.GetValueOrDefault((connectionId, deviceId)), current))
                    {
                        logger?.LogWarning("Mobile key confirmation timed out for connection {ConnectionId}.", connectionId);
                        _DropStream(connectionId, deviceId, streamId);
                    }
                }
            }, null, _ConfirmationTimeout, Timeout.InfiniteTimeSpan);
        }

        await _SendAsync(connectionId, deviceId, streamId, () =>
        {
            lock (_gate)
            {
                if (!_streams.TryGetValue((connectionId, deviceId), out var current) || current.StreamId != streamId || current.Cipher is not { } cipher)
                {
                    return null;
                }

                current.ReadySent = true;
                logger?.LogInformation("Sending mobile ready for connection {ConnectionId}.", connectionId);
                return cipher.Seal("{\"type\":\"ready\",\"id\":\"ready\",\"body\":{}}"u8);
            }
        }).ConfigureAwait(false);
    }

    // Under _gate. The reply for the phone once the handshake holds, or null for every refusal alike.
    private byte[]? _Handshake(string connectionId, string deviceId, _Stream stream, byte[] frame)
    {
        logger?.LogInformation("Mobile handshake received for connection {ConnectionId}.", connectionId);
        if (!_keys.TryGetCockpitKey(connectionId, out var key))
        {
            logger?.LogWarning("Mobile handshake cannot read the Cockpit key for connection {ConnectionId}.", connectionId);
            return null;
        }

        using (key)
        {
            _pending.TryGetValue(connectionId, out var pending);
            var phones = _Phones(connectionId);
            if (!MobileHandshake.TryAccept(frame, key!, stream.StreamId, pending?.Payload,
                    phonePub => phones.Any(phone => phone.DeviceId == deviceId && phone.PhonePub == Base64Url.EncodeToString(phonePub)) && _keys.IsPaired(connectionId, phonePub),
                    out var result))
            {
                logger?.LogWarning("Mobile handshake refused for connection {ConnectionId}.", connectionId);
                return null;
            }

            if (result is null)
            {
                return null;
            }

            if (result.NewlyPaired && pending is not null)
            {
                stream.Pairing = pending;
                stream.PhonePub = result.PhonePub;
                pending.DeviceId = deviceId;
            }

            stream.Cipher = result.Cipher;
            logger?.LogInformation("Mobile handshake accepted for connection {ConnectionId}; awaiting key confirmation.", connectionId);
            return result.Reply;
        }
    }

    // Under _gate. The phone's door onto the assistant while it is paired; null for a phone this Cockpit does not hold.
    private MobileAssistantChannel? _Channel(string connectionId, string deviceId)
    {
        if (_channels.TryGetValue((connectionId, deviceId), out var channel))
        {
            return channel;
        }

        if (_Phones(connectionId).FirstOrDefault(phone => phone.DeviceId == deviceId) is not { } paired
            || AssistantChannelAccess.ForSingleUser(deviceId).Access is not { } access)
        {
            return null;
        }

        var gateway = openChannel(new AssistantChannelContribution
        {
            Id = $"depot-mobile-{connectionId}-{deviceId}",
            Name = paired.Name,
            Access = access,
            Verbosity = AssistantChannelVerbosity.Everything,
        });
        channel = new MobileAssistantChannel(deviceId, gateway, plaintext => _SendToPhone(connectionId, deviceId, plaintext),
            Environment.MachineName, System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3), logger, host, _time);
        _channels[(connectionId, deviceId)] = channel;
        return channel;
    }

    // A phone gets at most this many frames waiting at once; past that a send is refused rather than queued, so one
    // phone that keeps asking cannot hold the relay or the memory of the others.
    private const int _MaxWaitingSends = 32;

    // Only over a stream whose phone proved its keys with a frame that opened (PROTOCOL.md § 9.4). The frame is sealed
    // when its turn to leave comes, and not at all once that stream is gone.
    private bool _SendToPhone(string connectionId, string deviceId, byte[] plaintext)
    {
        string streamId;
        lock (_gate)
        {
            if (!_streams.TryGetValue((connectionId, deviceId), out var stream) || stream is not { Cipher: not null, Confirmed: true }
                || stream.WaitingSends >= _MaxWaitingSends)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                return false;
            }

            streamId = stream.StreamId;
            stream.WaitingSends++;
        }

        _ = _SendAsync(connectionId, deviceId, streamId, () =>
        {
            lock (_gate)
            {
                try
                {
                    return _streams.TryGetValue((connectionId, deviceId), out var current)
                        && current is { Confirmed: true, Cipher: { } cipher } && current.StreamId == streamId
                        ? cipher.Seal(plaintext)
                        : null;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }, () =>
        {
            CryptographicOperations.ZeroMemory(plaintext);
            lock (_gate)
            {
                if (_streams.TryGetValue((connectionId, deviceId), out var current) && current.StreamId == streamId)
                {
                    current.WaitingSends--;
                }
            }
        });
        return true;
    }

    private async Task _SendAsync(string connectionId, string deviceId, string streamId, Func<byte[]?> frame, Action? done = null)
    {
        try
        {
            await _SendAsync(connectionId, deviceId, streamId, frame).ConfigureAwait(false);
        }
        finally
        {
            done?.Invoke();
        }
    }

    private async Task _SendAsync(string connectionId, string deviceId, string streamId, Func<byte[]?> frame)
    {
        MobileRelay? relay;
        lock (_gate)
        {
            _relays.TryGetValue(connectionId, out relay);
        }

        try
        {
            if (send is not null)
            {
                await send(connectionId, streamId, frame).ConfigureAwait(false);
            }
            else if (relay is not null)
            {
                await relay.SendAsync(streamId, frame, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _DropStream(connectionId, deviceId, streamId);
            }

            logger?.LogWarning("Sending to a phone over the relay failed for connection {ConnectionId}: {Reason}", connectionId, exception.GetType().Name);
        }
    }

    // Under _gate. The phone's key goes here, so nothing it sends opens a stream again; Depot keeps its own grant.
    private void _Forget(string connectionId, string deviceId)
    {
        _DropStream(connectionId, deviceId);
        _RemoveKey(connectionId, deviceId);
    }

    private void _RemoveKey(string connectionId, string deviceId)
    {
        var phones = _Phones(connectionId);
        foreach (var phone in phones.Where(phone => phone.DeviceId == deviceId))
        {
            _keys.RemovePhone(connectionId, Base64Url.DecodeFromChars(phone.PhonePub));
        }

        _Save(connectionId, [.. phones.Where(phone => phone.DeviceId != deviceId)]);

        // A forgotten phone, or one pairing again under the same id: its channel and what that channel saw go with its key.
        if (_channels.Remove((connectionId, deviceId), out var channel))
        {
            channel.Dispose();
        }
    }

    // Under _gate. With a stream id, only when that is still the device's current stream.
    private void _DropStream(string connectionId, string deviceId, string? streamId = null)
    {
        if (_streams.TryGetValue((connectionId, deviceId), out var stream) && (streamId is null || stream.StreamId == streamId))
        {
            _streams.Remove((connectionId, deviceId));
            stream.ConfirmationTimer?.Dispose();
            stream.Cipher?.Dispose();
            if (stream.Pairing is { } pending && ReferenceEquals(_pending.GetValueOrDefault(connectionId), pending))
            {
                _pending.Remove(connectionId);
                pending.Payload.Discard();
            }
            _channels.GetValueOrDefault((connectionId, deviceId))?.StreamEnded();
        }
    }

    private void _DropChannels(string connectionId)
    {
        foreach (var key in _channels.Keys.Where(key => key.Connection == connectionId).ToList())
        {
            _channels.Remove(key, out var channel);
            channel!.Dispose();
        }
    }

    private void _DropStreams(string connectionId)
    {
        foreach (var key in _streams.Keys.Where(key => key.Connection == connectionId).ToList())
        {
            _DropStream(key.Connection, key.Device);
        }
    }

    private void _Discard(string connectionId)
    {
        lock (_gate)
        {
            if (_pending.Remove(connectionId, out var pending))
            {
                pending.Payload.Discard();
                if (pending.DeviceId is { } device && _streams.TryGetValue((connectionId, device), out var stream)
                    && ReferenceEquals(stream.Pairing, pending))
                {
                    _DropStream(connectionId, device);
                }
            }
        }
    }

    private async Task<IReadOnlyList<_Device>?> _DepotDevicesAsync(DepotConnectionRegistration connection, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient(hostHandler(connection)) { Timeout = TimeSpan.FromSeconds(30) };
            using var response = await http.GetAsync($"{connection.Url.TrimEnd('/')}/cockpit/mobile/devices", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<List<_Device>>(DepotChannel.Json, cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger?.LogDebug(exception, "Reading the phones of {Connection} failed.", connection.Name);
            return null;
        }
    }

    private async Task<string?> _UserIdAsync(DepotConnectionRegistration connection, CancellationToken cancellationToken)
    {
        // Depot keeps /cockpit/me behind the plain depot scope; a depot.host token gets a 403 there and no relay would start.
        using var http = new HttpClient(userHandler(connection)) { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await http.GetAsync($"{connection.Url.TrimEnd('/')}/cockpit/me", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger?.LogWarning("Reading the account of {Connection} failed (HTTP {Status}), so its mobile relay does not start.",
                connection.Name, (int)response.StatusCode);
            return null;
        }

        return (await response.Content.ReadFromJsonAsync<_Me>(DepotChannel.Json, cancellationToken).ConfigureAwait(false))?.UserId;
    }

    private DepotConnectionRegistration _Connection(string connectionId) =>
        connections().FirstOrDefault(connection => connection.Id == connectionId)
        ?? throw new ArgumentException("That Depot connection is no longer configured.", nameof(connectionId));

    private List<MobilePairedPhone> _Phones(string connectionId) => storage.Get<List<MobilePairedPhone>>(_PhonesName(connectionId)) ?? [];

    private void _Save(string connectionId, List<MobilePairedPhone> phones) => storage.Set(_PhonesName(connectionId), phones);

    private static string _PhonesName(string connectionId) => $"mobilePairedPhones-{connectionId}";

    private static string _AccountLink(DepotConnectionRegistration connection) => $"{connection.Url.TrimEnd('/')}/account/mobile";

    // An older Depot names no phone (DEP-628 has no name in devicePaired): the start of its device id stands in.
    private static string _ShortName(string deviceId) => $"Phone {deviceId[..Math.Min(8, deviceId.Length)]}";

    private sealed class _Pending(PairingPayload payload)
    {
        public PairingPayload Payload { get; } = payload;

        public string? DeviceId { get; set; }

        public string? DeviceName { get; set; }

        public bool Paired { get; set; }
    }

    private sealed class _Stream(string streamId)
    {
        public string StreamId { get; } = streamId;

        public MobileFrameCipher? Cipher { get; set; }

        public bool Confirmed { get; set; }

        public bool ReadySent { get; set; }

        public long? ConfirmationStartedAt { get; set; }

        public ITimer? ConfirmationTimer { get; set; }

        public _Pending? Pairing { get; set; }

        public byte[]? PhonePub { get; set; }

        public int WaitingSends { get; set; }
    }

    private sealed class _NoSubscription : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed record _Issued(string PairingId, string DepotUrl, int ExpiresIn);

    private sealed record _Device(string Id, string? Name);

    private sealed record _Me(string UserId);
}

internal sealed record MobilePairedPhone(string DeviceId, string Name, string PhonePub, DateTimeOffset PairedAt);
