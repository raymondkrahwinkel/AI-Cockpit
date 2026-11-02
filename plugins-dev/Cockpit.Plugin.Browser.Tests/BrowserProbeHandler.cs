using System.Net;

namespace Cockpit.Plugin.Browser.Tests;

internal sealed class BrowserProbeHandler : HttpClientHandler
{
    public HttpStatusCode? LastStatus { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        LastStatus = response.StatusCode;
        return response;
    }
}
