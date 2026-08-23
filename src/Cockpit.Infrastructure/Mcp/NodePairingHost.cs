using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

// AC-792: the pairing handshake's network surface — one HTTPS listener for /pair/*, on only when the node
// master switch is on, its own listener since these sit outside McpAuthMiddleware by necessity. Unauthenticated
// surface is bounded: /pair/request only creates a pending pairing needing operator Confirm.
internal sealed class NodePairingHost : IHostedService, INodePairingEndpoint, ISingletonService, IAsyncDisposable
{
    private readonly INodeEndpointSettingsStore _settings;
    private readonly INodePairingBroker _broker;
    private readonly NodeSelfSignedCertificate _certificate;
    private readonly INodeVisibilityPolicy _visibility;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<NodePairingHost> _logger;

    private WebApplication? _app;

    public NodePairingHost(
        INodeEndpointSettingsStore settings,
        INodePairingBroker broker,
        NodeSelfSignedCertificate certificate,
        INodeVisibilityPolicy visibility,
        ILoggerFactory loggerFactory)
    {
        _settings = settings;
        _broker = broker;
        _certificate = certificate;
        _visibility = visibility;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<NodePairingHost>();
    }

    public string? Address { get; private set; }

    // The port Kestrel actually bound, kept apart from Address ("is this listening" vs. "what does the operator
    // type") — a machine with no LAN interface has the first without the second. Also the test seam for real TLS.
    internal int? BoundPort { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return;
        }

        try
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Services.AddSingleton(_loggerFactory);
            builder.WebHost.ConfigureKestrel(options =>
                options.Listen(System.Net.IPAddress.Any, 0, listenOptions => listenOptions.UseHttps(_certificate.Value)));

            var app = builder.Build();
            _MapRoutes(app);

            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            _app = app;

            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            var bound = addresses?.Addresses.FirstOrDefault(address => address.StartsWith("https://", StringComparison.Ordinal));

            if (bound is null)
            {
                return;
            }

            BoundPort = new Uri(bound).Port;

            // Kestrel reports the wildcard bind ("https://0.0.0.0:PORT"), which means nothing to the operator of a
            // second machine — the same translation `CockpitMcpEndpointHost` does for its node URLs.
            if (NodeReachableAddress.Resolve() is { } reachableHost)
            {
                Address = $"https://{reachableHost}:{BoundPort}";
                _logger.LogInformation("Cockpit node pairing listening at {PairingUrl}.", Address);
            }
        }
        catch (Exception ex)
        {
            // A cockpit that cannot open its pairing port is still a working cockpit — it just cannot be paired
            // with this run, which the Security tab says by having no address to show.
            _logger.LogWarning(ex, "Could not start the node pairing endpoint.");
        }
    }

    private void _MapRoutes(WebApplication app)
    {
        app.MapPost("/pair/request", async (HttpContext context) =>
        {
            var request = await _ReadAsync<NodePairingRequest>(context).ConfigureAwait(false);
            if (request is null)
            {
                return _Problem(StatusCodes.Status400BadRequest, NodePairingError.InvalidToken, "Expected a JSON body with a controllerName.");
            }

            // Criterion 3: the same visibility check discovery's reply passes through, so a caller outside the
            // whitelist can't reach pairing by guessing the address. Fails closed on no remote address at all.
            var remoteAddress = context.Connection.RemoteIpAddress;
            if (remoteAddress is null || !await _visibility.IsAllowedAsync(remoteAddress, context.RequestAborted).ConfigureAwait(false))
            {
                return _Problem(StatusCodes.Status403Forbidden, NodePairingError.NotVisible,
                    "This cockpit does not accept pairing requests from that address.");
            }

            // The connection's address, not the one in the body: the body is the caller's word for where it is,
            // and this string ends up in the refusal the operator reads when a second controller shows up.
            var address = remoteAddress.ToString();

            try
            {
                var offer = await _broker.RequestAsync(request.ControllerName, address, context.RequestAborted).ConfigureAwait(false);
                return Results.Json(offer, NodePairingJson.Options);
            }
            catch (NodePairingException ex)
            {
                return _Problem(StatusCodes.Status409Conflict, ex.Problem);
            }
        });

        app.MapPost("/pair/claim", async (HttpContext context) =>
        {
            var claim = await _ReadAsync<NodePairingClaimRequest>(context).ConfigureAwait(false);
            if (claim is null)
            {
                return _Problem(StatusCodes.Status400BadRequest, NodePairingError.InvalidToken, "Expected a JSON body with a pairingId and claimToken.");
            }

            try
            {
                var grant = await _broker.ClaimAsync(claim.PairingId, claim.ClaimToken, context.RequestAborted).ConfigureAwait(false);
                return Results.Json(grant, NodePairingJson.Options);
            }
            catch (NodePairingException ex)
            {
                return _Problem(_StatusFor(ex.Error), ex.Problem);
            }
        });

        app.MapPost("/pair/unpair", async (HttpContext context) =>
        {
            // The only route here that takes a credential, and it takes the one the pairing granted: ending a
            // coupling from the far end is something only the party in that coupling may do.
            var settings = await _settings.LoadAsync(context.RequestAborted).ConfigureAwait(false);
            var header = context.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : header;

            if (string.IsNullOrEmpty(settings.SharedSecret) || !_ConstantTimeEquals(token, settings.SharedSecret))
            {
                return _Problem(StatusCodes.Status401Unauthorized, NodePairingError.InvalidToken, "That token is not this cockpit's node secret.");
            }

            await _broker.UnpairAsync(context.RequestAborted).ConfigureAwait(false);
            return Results.NoContent();
        });
    }

    // Pending answers 202, not an error status; AlreadyUsed and Expired share 410 but keep separate codes in the
    // body (criterion 2), since a caller branching on status alone would otherwise lose the distinction.
    private static int _StatusFor(string error) => error switch
    {
        NodePairingError.Pending => StatusCodes.Status202Accepted,
        NodePairingError.Expired or NodePairingError.AlreadyUsed or NodePairingError.Refused => StatusCodes.Status410Gone,
        _ => StatusCodes.Status401Unauthorized,
    };

    private static async Task<T?> _ReadAsync<T>(HttpContext context)
        where T : class
    {
        try
        {
            return await context.Request.ReadFromJsonAsync<T>(NodePairingJson.Options, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or BadHttpRequestException or IOException
            or InvalidOperationException or NotSupportedException)
        {
            // ReadFromJsonAsync throws for a non-JSON body; these routes are unauthenticated on a network
            // interface, so a 400 here keeps an error page off a socket strangers can reach.
            return null;
        }
    }

    private static IResult _Problem(int status, string error, string description) =>
        _Problem(status, new NodePairingProblem(error, description));

    private static IResult _Problem(int status, NodePairingProblem problem) =>
        Results.Json(problem, NodePairingJson.Options, statusCode: status);

    private static bool _ConstantTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            await _app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
