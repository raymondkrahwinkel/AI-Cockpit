namespace Cockpit.Core.Tests.Voice;

/// <summary>In-memory <see cref="HttpMessageHandler"/> test double: hands a canned response (or throws) instead of hitting the network, and records whether it was invoked at all.</summary>
internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public bool WasInvoked { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        WasInvoked = true;
        return Task.FromResult(respond(request));
    }
}
