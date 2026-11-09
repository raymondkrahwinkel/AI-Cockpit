using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Events;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot.Mobile;

// AC-2224: pairing a phone from a connection's row and keeping the phones paired to it. Depot issues the pairing id and
// carries the frames; whether a phone gets in is decided here, by MobileHandshake, never by what Depot says.
internal sealed class MobilePairing(
    Func<IReadOnlyList<DepotConnectionRegistration>> connections,
    Func<DepotConnectionRegistration, HttpMessageHandler> hostHandler,
    IPluginStorage storage,
    DepotEvents events,
    ILogger? logger = null) : IAsyncDisposable
{
    private readonly MobileKeyStore _keys = new(storage);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, _Pending> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MobileRelay> _relays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IDisposable> _deviceEvents = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Connection, string Device), _Stream> _streams = [];

    // Connects the relay of every connection that has a paired phone, so those phones can reach this Cockpit.
    public void Start()
    {
        foreach (var connection in connections().Where(connection => _Phones(connection.Id).Count > 0))
        {
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

            if (!pending.Payload.IsOpen())
            {
                return new(DepotPairingState.Expired, "This code has expired. Nothing was paired.");
            }

            return new(pending.DeviceId is null ? DepotPairingState.Open : DepotPairingState.Waiting, ExpiresAt: pending.Payload.ExpiresAt);
        }
    }

    public void Cancel(string connectionId)
    {
        _Discard(connectionId);
        _ = PruneAsync();
    }

    // The phones this Cockpit holds a key for. Depot's list, when it has one, supplies the names and drops a phone Depot
    // unpaired while this Cockpit was not listening.
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
                        _Forget(connectionId, phone.DeviceId);
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

        return new([.. _Phones(connectionId).Select(phone => new DepotPairedPhone(phone.DeviceId, phone.Name, phone.PairedAt))], _AccountLink(connection));
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
            _Discard(connection.Id);
            MobileRelay? relay;
            lock (_gate)
            {
                _relays.Remove(connection.Id, out relay);
                _DropStreams(connection.Id);
            }

            if (relay is not null)
            {
                await relay.DisposeAsync().ConfigureAwait(false);
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
            foreach (var connection in _streams.Keys.Select(key => key.Connection).Distinct().ToList())
            {
                _DropStreams(connection);
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

            var relay = new MobileRelay(connection, userId, () => hostHandler(connection),
                (streamId, deviceId) => _OnOpened(connection.Id, streamId, deviceId),
                (deviceId, frame) => _OnFrame(connection.Id, deviceId, frame),
                deviceId => _OnClosed(connection.Id, deviceId));
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

            await _DropRelayAsync(connection.Id).ConfigureAwait(false);
            logger?.LogWarning(exception, "Connecting the mobile relay for {Connection} failed.", connection.Name);
        }
    }

    private void _OnRelayClosed(string connectionId, MobileRelay closed)
    {
        lock (_gate)
        {
            if (_relays.TryGetValue(connectionId, out var current) && ReferenceEquals(current, closed))
            {
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
                _Forget(connectionId, deviceId);
                _ = PruneAsync();
            }
            else if (cockpitEvent.Kind == "devicePaired" && _pending.TryGetValue(connectionId, out var pending) && !pending.Paired)
            {
                pending.DeviceId = deviceId;
                pending.DeviceName = string.IsNullOrWhiteSpace(cockpitEvent.Name) ? null : cockpitEvent.Name;
            }
        }
    }

    // A new stream from a phone: its first frame will be the handshake, keyed to this stream id (PROTOCOL.md § 4).
    private void _OnOpened(string connectionId, string streamId, string deviceId)
    {
        lock (_gate)
        {
            _DropStream(connectionId, deviceId);
            _streams[(connectionId, deviceId)] = new _Stream(streamId);
        }
    }

    private void _OnClosed(string connectionId, string deviceId)
    {
        lock (_gate)
        {
            _DropStream(connectionId, deviceId);
        }
    }

    private void _OnFrame(string connectionId, string deviceId, byte[] frame)
    {
        byte[]? reply = null;
        lock (_gate)
        {
            if (!_streams.TryGetValue((connectionId, deviceId), out var stream))
            {
                return;
            }

            if (stream.Cipher is { } cipher)
            {
                // The content is AC-2227's; a frame that does not open ends the stream.
                if (!cipher.TryOpen(frame, out var plaintext))
                {
                    _DropStream(connectionId, deviceId);
                }

                CryptographicOperations.ZeroMemory(plaintext);
                return;
            }

            reply = _Handshake(connectionId, deviceId, stream, frame);
            if (reply is null)
            {
                _DropStream(connectionId, deviceId);
                return;
            }
        }

        _ = _SendAsync(connectionId, deviceId, reply);
    }

    // Under _gate. The reply for the phone once the handshake holds, or null for every refusal alike.
    private byte[]? _Handshake(string connectionId, string deviceId, _Stream stream, byte[] frame)
    {
        if (!_keys.TryGetCockpitKey(connectionId, out var key))
        {
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
                return null;
            }

            if (result!.NewlyPaired)
            {
                _RemoveKey(connectionId, deviceId);
                _keys.AddPhone(connectionId, result.PhonePub);
                var name = pending!.DeviceName ?? _ShortName(deviceId);
                _Save(connectionId, [.. _Phones(connectionId), new MobilePairedPhone(deviceId, name, Base64Url.EncodeToString(result.PhonePub), DateTimeOffset.UtcNow)]);
                pending.DeviceId = deviceId;
                pending.DeviceName = name;
                pending.Paired = true;
            }

            stream.Cipher = result.Cipher;
            return result.Reply;
        }
    }

    private async Task _SendAsync(string connectionId, string deviceId, byte[] frame)
    {
        MobileRelay? relay;
        lock (_gate)
        {
            _relays.TryGetValue(connectionId, out relay);
        }

        try
        {
            if (relay is not null)
            {
                await relay.SendAsync(deviceId, frame, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _DropStream(connectionId, deviceId);
            }

            logger?.LogDebug("Sending to a phone over the relay failed: {Reason}", exception.GetType().Name);
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
    }

    private void _DropStream(string connectionId, string deviceId)
    {
        if (_streams.Remove((connectionId, deviceId), out var stream))
        {
            stream.Cipher?.Dispose();
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
        using var http = new HttpClient(hostHandler(connection)) { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await http.GetAsync($"{connection.Url.TrimEnd('/')}/cockpit/me", cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync<_Me>(DepotChannel.Json, cancellationToken).ConfigureAwait(false))?.UserId
            : null;
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
