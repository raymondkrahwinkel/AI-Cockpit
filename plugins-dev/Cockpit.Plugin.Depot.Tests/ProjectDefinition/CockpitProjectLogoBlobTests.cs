using System.Net;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.ProjectDefinition;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

public class CockpitProjectLogoBlobTests
{
    private static readonly DepotConnectionRegistration _Connection =
        new("synvolution", "Synvolution", "https://depot.example.com");

    private sealed class _StubHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public HttpRequestMessage? LastRequest { get; private set; }
        public byte[]? LastRequestBody { get; private set; }
        public List<Uri> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(request.RequestUri ?? throw new InvalidOperationException("The request had no URI."));
            return _responses.Dequeue();
        }
    }

    [Fact]
    public async Task UploadAsync_Success_PostsForAnUploadThenPutsTheBytesToTheReturnedUrl()
    {
        var host = Substitute.For<ICockpitHost>();
        var handler = new _StubHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"uploads":[{"uploadUrl":"https://depot.example.com/blob/upload/abc"}]}""") },
            new HttpResponseMessage(HttpStatusCode.Created));
        using var httpClient = new HttpClient(handler);
        var bytes = new byte[] { 1, 2, 3 };

        var result = await CockpitProjectLogoBlob.UploadAsync(host, _Connection, "acme/cockpit", bytes, httpClient);

        Assert.Equal(PluginMcpToolCallOutcome.Success, result.Outcome);
        Assert.Equal("https://depot.example.com/sync/acme/cockpit/uploads", handler.Requests[0].ToString());
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Put, handler.LastRequest.Method);
        Assert.NotNull(handler.LastRequest.RequestUri);
        Assert.Equal("https://depot.example.com/blob/upload/abc", handler.LastRequest.RequestUri.ToString());
        Assert.Equal(bytes, handler.LastRequestBody);
        Assert.NotNull(handler.LastRequest.Content);
        Assert.Equal("image/png", handler.LastRequest.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task UploadAsync_PutReturnsAnErrorStatus_ReportsFailed()
    {
        var host = Substitute.For<ICockpitHost>();
        var handler = new _StubHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"uploads":[{"uploadUrl":"https://depot.example.com/blob/upload/abc"}]}""") },
            new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var httpClient = new HttpClient(handler);

        var result = await CockpitProjectLogoBlob.UploadAsync(host, _Connection, "acme/cockpit", [1], httpClient);

        Assert.Equal(PluginMcpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("403", result.Error);
    }

    [Fact]
    public async Task DeleteAsync_AlreadyGone_IsReportedAsSuccessRatherThanFailed()
    {
        // AC-763: a retried save (after an earlier attempt lost the checksum race) must not turn "already deleted"
        // into an error — see DeleteAsync's own remarks on idempotency.
        var host = Substitute.For<ICockpitHost>();
        var handler = new _StubHandler(new HttpResponseMessage(HttpStatusCode.NotFound));
        using var httpClient = new HttpClient(handler);

        var result = await CockpitProjectLogoBlob.DeleteAsync(host, _Connection, "acme/cockpit", httpClient);

        Assert.Equal(PluginMcpToolCallOutcome.Success, result.Outcome);
        Assert.Equal("https://depot.example.com/sync/acme/cockpit/delete?path=.cockpit%2Flogo.png&kind=artifact", handler.Requests.Single().ToString());
    }
}
