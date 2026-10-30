using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.BackendApi;
using Cockpit.Infrastructure.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Infrastructure.Swarm;

internal static class SwarmSessionEventsEndpoint
{
    public static void Map(IEndpointRouteBuilder routes, IServiceProvider services) =>
        SwarmNetworkPolicy.MapGroup(routes, services).MapGet("/session-events", (Func<HttpContext, Task>)(context => StreamAsync(context, services)));

    internal static async Task StreamAsync(HttpContext context, IServiceProvider services)
    {
        var caller = McpRequestContext.CurrentNodeCaller;
        if (caller is not { ByConnectKey: true, Capability: ConnectKeyCapability.Admin }
            || !(await services.GetRequiredService<SwarmMembershipStore>().LoadAsync(context.RequestAborted).ConfigureAwait(false))
                .Members.Any(member => string.Equals(caller.Label, SwarmKick.KeyLabel(member), StringComparison.Ordinal)))
        {
            context.Response.StatusCode = caller is null ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden;
            return;
        }

        await EventsEndpoint.StreamAsync(context, services, swarmAssistant: true).ConfigureAwait(false);
    }
}
