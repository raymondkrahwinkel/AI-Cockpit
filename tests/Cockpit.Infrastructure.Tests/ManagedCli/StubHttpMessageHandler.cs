using System.Net;

namespace Cockpit.Infrastructure.Tests.ManagedCli;

/// <summary>
/// A test <see cref="HttpMessageHandler"/> that answers each request from a supplied responder, so a managed-CLI
/// install can be exercised without a network. Counts calls so a test can assert a cache hit did not download.
/// </summary>
internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public int CallCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(responder(request));
    }

    public static HttpResponseMessage Bytes(byte[] payload) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
}
