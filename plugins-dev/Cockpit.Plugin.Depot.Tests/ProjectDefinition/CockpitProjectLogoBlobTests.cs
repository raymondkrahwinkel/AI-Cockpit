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

    private sealed class _StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public byte[]? LastRequestBody { get; private set; }
        public List<Uri> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(request.RequestUri!);
            return respond(request);
        }
    }

    [Fact]
    public async Task UploadAsync_Success_PostsForAnUploadThenPutsTheBytesToTheReturnedUrl()
    {
        var host = Substitute.For<ICockpitHost>();
        var handler = new _StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/uploads", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"uploads":[{"uploadUrl":"https://depot.example.com/blob/upload/abc"}]}""") }
            : new HttpResponseMessage(HttpStatusCode.Created));
        using var httpClient = new HttpClient(handler);
        var bytes = new byte[] { 1, 2, 3 };

        var result = await CockpitProjectLogoBlob.UploadAsync(host, _Connection, "acme/cockpit", bytes, httpClient);

        Assert.Equal(PluginMcpToolCallOutcome.Success, result.Outcome);
        Assert.Equal("https://depot.example.com/sync/acme/cockpit/uploads", handler.Requests[0].ToString());
        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal("https://depot.example.com/blob/upload/abc", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal(bytes, handler.LastRequestBody);
        Assert.Equal("image/png", handler.LastRequest.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task UploadAsync_PutReturnsAnErrorStatus_ReportsFailed()
    {
        var host = Substitute.For<ICockpitHost>();
        var handler = new _StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/uploads", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"uploads":[{"uploadUrl":"https://depot.example.com/blob/upload/abc"}]}""") }
            : new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var httpClient = new HttpClient(handler);

        var result = await CockpitProjectLogoBlob.UploadAsync(host, _Connection, "acme/cockpit", [1], httpClient);

        Assert.Equal(PluginMcpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("403", result.Error);
    }

    [Fact]
    public async Task DownloadAsync_Success_PostsForADownloadThenGetsTheBytesFromTheReturnedUrl()
    {
        var host = Substitute.For<ICockpitHost>();
        var expectedBytes = new byte[] { 137, 80, 78, 71 };
        var handler = new _StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/downloads", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"files":[{"downloadUrl":"https://depot.example.com/blob/download/xyz"}]}""") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(expectedBytes) });
        using var httpClient = new HttpClient(handler);

        var result = await CockpitProjectLogoBlob.DownloadAsync(host, _Connection, "acme/cockpit", httpClient);

        Assert.Equal(PluginMcpToolCallOutcome.Success, result.Outcome);
        Assert.Equal(expectedBytes, result.Bytes);
        Assert.Equal("https://depot.example.com/sync/acme/cockpit/downloads", handler.Requests[0].ToString());
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("https://depot.example.com/blob/download/xyz", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task DeleteAsync_AlreadyGone_IsReportedAsSuccessRatherThanFailed()
    {
        // AC-763: a retried save (after an earlier attempt lost the checksum race) must not turn "already deleted"
        // into an error — see DeleteAsync's own remarks on idempotency.
        var host = Substitute.For<ICockpitHost>();
        var handler = new _StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var httpClient = new HttpClient(handler);

        var result = await CockpitProjectLogoBlob.DeleteAsync(host, _Connection, "acme/cockpit", httpClient);

        Assert.Equal(PluginMcpToolCallOutcome.Success, result.Outcome);
        Assert.Equal("https://depot.example.com/sync/acme/cockpit/delete?path=.cockpit%2Flogo.png&kind=artifact", handler.Requests.Single().ToString());
    }
}
