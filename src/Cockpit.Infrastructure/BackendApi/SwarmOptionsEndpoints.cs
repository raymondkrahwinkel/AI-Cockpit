using System.Net;
using System.Text.Json.Serialization;
using Cockpit.Core.Mcp;
using Cockpit.Core.Swarm;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Infrastructure.BackendApi;

// AC-2279: the server's own Swarm page over fixed admin routes. Pairing stays on the server's own screen, and a new
// extra internal range must lie inside private address space: no route makes a public address internal.
internal static class SwarmOptionsEndpoints
{
    public static void Map(RouteGroupBuilder api, IServiceProvider services)
    {
        ISwarmOptions swarm() => services.GetRequiredService<ISwarmOptions>();
        ISwarmLeadership leadership() => services.GetRequiredService<ISwarmLeadership>();

        async Task<SwarmStatus> status(CancellationToken ct)
        {
            var settings = await swarm().LoadSettingsAsync(ct).ConfigureAwait(false);
            var leader = leadership();
            return new(await swarm().MachinesAsync(ct).ConfigureAwait(false), leader.Current, leader.IsSelf, leader.PendingTakeover,
                await swarm().RemovalStatusAsync(ct).ConfigureAwait(false), swarm().ConnectionFailure,
                await swarm().ConnectionsEnabledAsync(ct).ConfigureAwait(false), settings.ExtraInternalCidrs,
                settings.WhenALowerRankComesBack == SwarmLowerRankReturn.JoinAtBack, settings.ManualAddresses);
        }

        // A server built without Swarm answers its page as absent rather than failing.
        var group = api.MapGroup("/options/swarm");
        group.AddEndpointFilter(async (context, next) =>
            services.GetService<ISwarmOptions>() is null || services.GetService<ISwarmLeadership>() is null
                ? BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_swarm", "This server has no Swarm.")
                : await next(context).ConfigureAwait(false));

        group.MapGet("", async (CancellationToken ct) => Results.Json(await status(ct).ConfigureAwait(false), ConnectKeyEndpoints.Json)).RequireAdmin();

        group.MapPut("", async (HttpRequest request, CancellationToken ct) =>
        {
            var (body, refused) = await ProfileEndpoints.ReadBodyAsync<SwarmOptionsBody>(request, "The Swarm settings name only extraInternalCidrs and joinAtBack.", ct).ConfigureAwait(false);
            if (body is null)
            {
                return refused;
            }

            return await _UnderGateAsync(request.HttpContext, async () =>
            {
                var stored = await swarm().LoadSettingsAsync(ct).ConfigureAwait(false);
                var kept = stored.ExtraInternalCidrs.Select(_Normalized).ToHashSet(StringComparer.Ordinal);
                if (body.ExtraInternalCidrs.Any(cidr => !IPNetwork.TryParse(cidr.Trim(), out var network)
                    || (!kept.Contains(network.ToString()) && !PrivateRanges.Any(range => network.PrefixLength >= range.PrefixLength && range.Contains(network.BaseAddress)))))
                {
                    return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request",
                        "A new internal range must lie inside 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 100.64.0.0/10 or fc00::/7.");
                }

                await swarm().SaveSettingsAsync(stored with
                {
                    ExtraInternalCidrs = [.. body.ExtraInternalCidrs.Select(cidr => cidr.Trim())],
                    WhenALowerRankComesBack = body.JoinAtBack ? SwarmLowerRankReturn.JoinAtBack : SwarmLowerRankReturn.AnnouncedTakeover,
                }, ct).ConfigureAwait(false);
                return Results.Json(await status(ct).ConfigureAwait(false), ConnectKeyEndpoints.Json);
            }, services).ConfigureAwait(false);
        }).RequireAdmin().Audited("options_swarm", services);

        group.MapPost("/members/{id}/remove", (string id, HttpContext context, CancellationToken ct) =>
            SwarmMemberId.TryParse(id, out var member)
                ? _UnderGateAsync(context, () => _DoneAsync(swarm().KickAsync(member, ct)), services)
                : Task.FromResult(BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "A swarm member id is 64 hex characters.")))
            .RequireAdmin().Audited("swarm_remove_member", services);

        group.MapPost("/reset", (HttpContext context, CancellationToken ct) =>
            _UnderGateAsync(context, async () => Results.Json(new { backup = await swarm().ResetAsync(ct).ConfigureAwait(false) }), services))
            .RequireAdmin().Audited("swarm_reset", services);

        group.MapPost("/leadership", (HttpContext context, CancellationToken ct) =>
            _UnderGateAsync(context, () => _DoneAsync(leadership().ClaimLeadershipAsync(ct)), services))
            .RequireAdmin().Audited("swarm_claim_leadership", services);

        group.MapPost("/retry", (HttpContext context, CancellationToken ct) =>
            _UnderGateAsync(context, () => _DoneAsync(swarm().RetryAsync(ct)), services))
            .RequireAdmin().Audited("swarm_retry", services);
    }

    private static async Task<IResult> _UnderGateAsync(HttpContext context, Func<Task<IResult>> change, IServiceProvider services)
    {
        await OptionsSettingsEndpoints.EditGate.WaitAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
            return await OptionsSettingsEndpoints.RefuseIfNoLongerAdminAsync(context, services).ConfigureAwait(false) ?? await change().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or NodePairingException)
        {
            return BackendApiRoutes.Error(StatusCodes.Status409Conflict, "swarm_refused", exception.Message);
        }
        finally
        {
            OptionsSettingsEndpoints.EditGate.Release();
        }
    }

    private static async Task<IResult> _DoneAsync(Task action)
    {
        await action.ConfigureAwait(false);
        return Results.Json(new { ok = true });
    }

    // Where a range added over the API may lie: private, carrier-grade NAT (a VPN such as Tailscale) and unique local.
    private static readonly IPNetwork[] PrivateRanges =
        [IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("172.16.0.0/12"), IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("100.64.0.0/10"), IPNetwork.Parse("fc00::/7")];

    private static string? _Normalized(string cidr) => IPNetwork.TryParse(cidr.Trim(), out var network) ? network.ToString() : null;
}

internal sealed record SwarmOptionsBody([property: JsonRequired] IReadOnlyList<string> ExtraInternalCidrs, [property: JsonRequired] bool JoinAtBack);

internal sealed record SwarmStatus(IReadOnlyList<SwarmMachine> Machines, SwarmMember? Leader, bool LeaderIsSelf, SwarmTakeover? PendingTakeover,
    string RemovalStatus, string ConnectionFailure, bool ConnectionsEnabled, IReadOnlyList<string> ExtraInternalCidrs, bool JoinAtBack,
    IReadOnlyList<SwarmManualMember> ManualAddresses);
