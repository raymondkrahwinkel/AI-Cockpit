namespace Cockpit.Plugins.Abstractions.Mcp;

// The default ICockpitHost.CreateAuthorizedHandler hands out on a host that predates it (AC-1587): it refuses every
// request rather than sending one without the credential the plugin asked for.
internal sealed class UnavailableAuthorizedHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new HttpRequestException("This host cannot send authorized requests for a plugin's MCP server.");
}
