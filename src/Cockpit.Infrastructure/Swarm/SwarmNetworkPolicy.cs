using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Swarm;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Swarm;

// AC-1916: the swarm's "internal network only" rule, one entry per place it holds: discovery (`IsAllowedAsync` as
// `NodeDiscoveryClient.FindAsync`'s `accept`), outgoing connections (`ConnectAsync`), the swarm routes (this
// filter, on `MapGroup`) and a member's address update (`KeepInternalAsync`).
internal sealed class SwarmNetworkPolicy : IEndpointFilter, ISingletonService
{
    private readonly SwarmSettingsStore _settings;
    private readonly NodeAccessAuditLog _audit;
    private readonly ILogger<SwarmNetworkPolicy> _logger;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;

    public SwarmNetworkPolicy(SwarmSettingsStore settings, NodeAccessAuditLog audit, ILogger<SwarmNetworkPolicy> logger)
        : this(settings, audit, logger, Dns.GetHostAddressesAsync)
    {
    }

    // Test seam: a scripted resolver, so a name can resolve differently from one call to the next.
    internal SwarmNetworkPolicy(SwarmSettingsStore settings, NodeAccessAuditLog audit, ILogger<SwarmNetworkPolicy> logger, Func<string, CancellationToken, Task<IPAddress[]>> resolve)
    {
        _settings = settings;
        _audit = audit;
        _logger = logger;
        _resolve = resolve;
    }

    // Every swarm route is mapped on this group, so none of them answers a caller outside the internal ranges.
    public static RouteGroupBuilder MapGroup(IEndpointRouteBuilder routes, IServiceProvider services) =>
        routes.MapGroup("/swarm").AddEndpointFilter(services.GetRequiredService<SwarmNetworkPolicy>());

    public async Task<bool> IsAllowedAsync(IPAddress address, CancellationToken cancellationToken = default)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        return InternalNetworkRanges.IsInternal(address, InternalNetworkRanges.ParseExtra(settings.ExtraInternalCidrs));
    }

    // Resolves on every call, never from an earlier answer, so a name that once pointed inside and now points
    // outside is refused. Of a mixed answer only the internal addresses are returned.
    public async Task<IPAddress[]> ResolveInternalAsync(string host, CancellationToken cancellationToken = default)
    {
        var resolved = await _resolve(host, cancellationToken).ConfigureAwait(false);
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var extra = InternalNetworkRanges.ParseExtra(settings.ExtraInternalCidrs);
        var internalAddresses = resolved.Where(address => InternalNetworkRanges.IsInternal(address, extra)).ToArray();

        return internalAddresses.Length > 0
            ? internalAddresses
            : throw NodePairingException.For(
                SwarmNetworkError.AddressNotInternal,
                $"{host} is not on the internal network, so this cockpit will not connect to it. A VPN's range can be added as an internal range.");
    }

    // A `SocketsHttpHandler.ConnectCallback`: dials only the internal addresses `host` resolves to right now.
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await ResolveInternalAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // A member's announced address replaces its last one only when it is internal.
    public async Task<IPAddress> KeepInternalAsync(IPAddress last, IPAddress announced, CancellationToken cancellationToken = default)
    {
        if (await IsAllowedAsync(announced, cancellationToken).ConfigureAwait(false))
        {
            return announced;
        }

        _logger.LogWarning("{Outcome}: a member announced {Announced}, outside the internal network; keeping {Last}.", SwarmNetworkError.AddressUpdateIgnored, announced, last);
        return last;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.Connection.RemoteIpAddress is { } caller && await IsAllowedAsync(caller, http.RequestAborted).ConfigureAwait(false))
        {
            return await next(context).ConfigureAwait(false);
        }

        var remote = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        await _audit.RecordAsync(new NodeAccessAuditEntry(DateTimeOffset.UtcNow, "not checked", null, remote, http.Request.Path, SwarmNetworkError.CallerNotInternal), CancellationToken.None).ConfigureAwait(false);
        return Results.Json(
            new NodePairingProblem(SwarmNetworkError.CallerNotInternal, "This cockpit answers swarm calls only from the internal network."),
            NodePairingJson.Options,
            statusCode: StatusCodes.Status403Forbidden);
    }
}
