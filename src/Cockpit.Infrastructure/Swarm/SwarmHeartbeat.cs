using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Swarm;
using Cockpit.Infrastructure.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Swarm;

internal sealed class SwarmHeartbeat(
    SwarmHost host,
    SwarmIdentity identity,
    NodeSelfSignedCertificate certificate,
    SwarmMembershipStore store,
    SwarmPropagation propagation,
    SwarmNetworkPolicy network,
    SwarmJoinClient join,
    IMcpServerStore servers,
    INodeEndpointSettingsStore settings,
    ConnectKeyVerifier keys,
    SwarmReachability reachability,
    ILogger<SwarmHeartbeat> logger) : BackgroundService, ISingletonService
{
    private readonly SemaphoreSlim _tick = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<SwarmMemberId, SwarmHeartbeatUpdate> _updates = [];
    private bool _asleep;
    internal TimeProvider Time { get; set; } = TimeProvider.System;

    internal SwarmHeartbeatUpdate? Latest(SwarmMemberId member)
    {
        lock (_gate) return _updates.GetValueOrDefault(member);
    }

    internal async Task<SwarmRecord> OwnAsync(bool asleep, CancellationToken ct)
    {
        var membership = await store.LoadAsync(ct);
        var local = await settings.LoadAsync(ct);
        var update = new SwarmHeartbeatUpdate("heartbeat", identity.MemberId,
            [NodeReachableAddress.Resolve() ?? "127.0.0.1"], local.Port, asleep, membership.Sequence,
            SwarmPropagation.Prefix(membership.Log), Time.GetUtcNow());
        return identity.Sign(JsonSerializer.SerializeToElement(update, SwarmRecordJson.Options));
    }

    internal async Task<SwarmHeartbeatUpdate?> AcceptAsync(SwarmRecord record, SwarmMemberId peer, string pin, CancellationToken ct)
    {
        var membership = await store.LoadAsync(ct);
        var member = membership.Members.FirstOrDefault(item => item.Id == peer);
        if (member is null || !record.Verify(membership.PublicKeyOf)) return null;
        var update = record.Payload.Deserialize<SwarmHeartbeatUpdate>(SwarmRecordJson.Options);
        if (update is null || update.Kind != "heartbeat" || update.Member != peer
            || !record.Signatures.Any(signature => signature.MemberId == update.Member)
            || update.Port is <= 0 or > 65535 || update.Sequence < 0 || update.At > Time.GetUtcNow() + SwarmReachability.Lease)
            return null;
        var transport = (await servers.LoadAsync(ct)).FirstOrDefault(server =>
            server.Name.StartsWith(NodeServerName.PrefixFor(member.Name), StringComparison.Ordinal)
            && server.PinnedCertificateFingerprint == pin);
        if (transport is null) return null;
        var addresses = new List<string>();
        foreach (var address in update.Addresses)
        {
            if (!IPAddress.TryParse(address, out var parsed)) continue;
            var kept = await network.KeepInternalAsync(IPAddress.None, parsed, ct);
            if (kept.Equals(parsed)) addresses.Add(address);
        }
        lock (_gate)
        {
            if (_updates.TryGetValue(peer, out var previous) && update.At <= previous.At) return null;
            _updates[peer] = update with { Addresses = addresses.Count == 0 ? member.Addresses : addresses,
                Port = addresses.Count == 0 ? member.Port : update.Port };
        }
        reachability.Answer(peer, HttpStatusCode.OK, update.Asleep);
        return update;
    }

    public void MapRoutes(IEndpointRouteBuilder routes, IServiceProvider services)
    {
        var group = SwarmNetworkPolicy.MapGroup(routes, services);
        group.MapPost("/heartbeat", (Delegate)((HttpContext context) => HandleAsync(context, async peer =>
        {
            var record = await context.Request.ReadFromJsonAsync<SwarmRecord>(SwarmRecordJson.Options, context.RequestAborted);
            var pin = NodeCertificatePin.FingerprintOf((await context.Connection.GetClientCertificateAsync(context.RequestAborted))!);
            if (record is null || await AcceptAsync(record, peer, pin, context.RequestAborted) is null) return Results.StatusCode(403);
            return Results.Json(await OwnAsync(false, context.RequestAborted), SwarmRecordJson.Options);
        })));
        group.MapGet("/records", (Delegate)((HttpContext context) => HandleAsync(context, async _ =>
        {
            if (!long.TryParse(context.Request.Query["since"], out var since)) return Results.BadRequest();
            var delta = await propagation.RecordsAsync(since, context.Request.Query["prefix"], context.RequestAborted);
            return Results.Json(delta, SwarmRecordJson.Options);
        })));
        group.MapPost("/records", (Delegate)((HttpContext context) => HandleAsync(context, async _ =>
        {
            var delta = await context.Request.ReadFromJsonAsync<SwarmRecords>(SwarmRecordJson.Options, context.RequestAborted);
            if (delta is null) return Results.BadRequest();
            await propagation.ApplyAsync(delta, context.RequestAborted);
            return Results.Ok();
        })));
    }

    private async Task<IResult> HandleAsync(HttpContext context, Func<SwarmMemberId, Task<IResult>> action)
    {
        if (host.Kind == SwarmHostKind.Server) return Results.StatusCode(403);
        var ct = context.RequestAborted;
        var peer = await context.Connection.GetClientCertificateAsync(ct);
        if (peer is null) return Results.Unauthorized();
        var id = SwarmMemberId.FromPublicKey(peer.PublicKey.ExportSubjectPublicKeyInfo());
        var membership = await store.LoadAsync(ct);
        var member = membership.Members.FirstOrDefault(item => item.Id == id);
        var pin = NodeCertificatePin.FingerprintOf(peer);
        if (member is null || !(await servers.LoadAsync(ct)).Any(server =>
            server.Name.StartsWith(NodeServerName.PrefixFor(member.Name), StringComparison.Ordinal) && server.PinnedCertificateFingerprint == pin))
            return Results.StatusCode(403);
        var token = context.Request.Headers.Authorization.ToString();
        if (!token.StartsWith("Bearer ", StringComparison.Ordinal)) return Results.Unauthorized();
        var caller = await keys.AuthenticateAsync(token[7..], null, context.Connection.RemoteIpAddress!.ToString(), ct);
        if (caller is not { Capability: ConnectKeyCapability.Admin }) return Results.Unauthorized();
        try
        {
            return await action(id);
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(ex, "Swarm record synchronization refused for {Member}.", id);
            return Results.Conflict(new { error = ex.Message });
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }
    }

    internal async Task TickAsync(bool asleep = false, CancellationToken ct = default)
    {
        await _tick.WaitAsync(ct);
        try
        {
            var membership = await store.LoadAsync(ct);
            await Task.WhenAll(membership.Members.Where(member => member.Id != identity.MemberId).Select(async member =>
            {
                try
                {
                    var transport = (await servers.LoadAsync(ct)).FirstOrDefault(server =>
                        server.Name.StartsWith(NodeServerName.PrefixFor(member.Name), StringComparison.Ordinal));
                    if (transport is null)
                    {
                        await join.ConnectMemberAsync(membership, member, ct);
                        transport = (await servers.LoadAsync(ct)).FirstOrDefault(server =>
                            server.Name.StartsWith(NodeServerName.PrefixFor(member.Name), StringComparison.Ordinal));
                    }
                    if (transport?.PinnedCertificateFingerprint is not { } pin) return;
                    SwarmHeartbeatUpdate? last;
                    lock (_gate) last = _updates.GetValueOrDefault(member.Id);
                    using var handler = new SocketsHttpHandler { ConnectCallback = network.ConnectAsync };
                    handler.SslOptions.ClientCertificates = new X509CertificateCollection { certificate.Value };
                    handler.SslOptions.RemoteCertificateValidationCallback = (_, remote, _, _) =>
                    {
                        if (remote is null || NodeCertificatePin.FingerprintOf(remote) != pin) return false;
                        using var peer = X509CertificateLoader.LoadCertificate(remote.GetRawCertData());
                        return SwarmMemberId.FromPublicKey(peer.PublicKey.ExportSubjectPublicKeyInfo()) == member.Id;
                    };
                    var address = last?.Addresses.FirstOrDefault() ?? member.Addresses.FirstOrDefault();
                    if (address is null) return;
                    using var client = new HttpClient(handler) { BaseAddress = new UriBuilder("https", address, last?.Port ?? member.Port).Uri,
                        Timeout = SwarmReachability.Interval };
                    client.DefaultRequestHeaders.Authorization = new("Bearer", transport.ApiKey);
                    using var response = await client.PostAsJsonAsync("swarm/heartbeat", await OwnAsync(asleep, ct), SwarmRecordJson.Options, ct);
                    if (!response.IsSuccessStatusCode) return;
                    var record = await response.Content.ReadFromJsonAsync<SwarmRecord>(SwarmRecordJson.Options, ct);
                    if (record is null || await AcceptAsync(record, member.Id, pin, ct) is not { } update) return;
                    var current = await store.LoadAsync(ct);
                    if (update.Sequence >= current.Sequence)
                    {
                        var prefix = SwarmPropagation.Prefix(current.Log);
                        using var records = await client.GetAsync($"swarm/records?since={current.Sequence}&prefix={prefix}", ct);
                        records.EnsureSuccessStatusCode();
                        var delta = await records.Content.ReadFromJsonAsync<SwarmRecords>(SwarmRecordJson.Options, ct);
                        if (delta is not null) await propagation.ApplyAsync(delta, ct);
                    }
                    else
                    {
                        var delta = await propagation.RecordsAsync(update.Sequence, update.Prefix, ct);
                        using var pushed = await client.PostAsJsonAsync("swarm/records", delta, SwarmRecordJson.Options, ct);
                        pushed.EnsureSuccessStatusCode();
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException
                    or InvalidDataException or JsonException or NodePairingException)
                {
                    logger.LogWarning(ex, "Swarm heartbeat or synchronization failed for {Member}.", member.Id);
                }
            }));
        }
        finally
        {
            _tick.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (host.Kind == SwarmHostKind.Server) return;
        if (OperatingSystem.IsWindows()) Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        using var timer = new PeriodicTimer(SwarmReachability.Interval, Time);
        try
        {
            do
            {
                if (!_asleep) await TickAsync(ct: stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        finally
        {
            if (OperatingSystem.IsWindows()) Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs args)
    {
        _asleep = args.Mode == Microsoft.Win32.PowerModes.Suspend;
        try
        {
            await TickAsync(_asleep);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not announce swarm power transition.");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (host.Kind == SwarmHostKind.Desktop) await TickAsync(true, cancellationToken);
    }
}

internal sealed record SwarmHeartbeatUpdate(string Kind, SwarmMemberId Member, IReadOnlyList<string> Addresses, int Port,
    bool Asleep, long Sequence, string? Prefix, DateTimeOffset At);
