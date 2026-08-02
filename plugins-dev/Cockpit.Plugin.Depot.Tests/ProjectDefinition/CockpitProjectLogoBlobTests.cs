using System.Net;
using Cockpit.Plugin.Depot.ProjectDefinition;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

public class CockpitProjectLogoBlobTests
{
    private sealed class _StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public byte[]? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return respond(request);
        }
    }

    [Fact]
    public async Task UploadAsync_Success_CallsRequestUploadThenPutsTheBytesToTheReturnedUrl()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "request_upload", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success("""{"uploadUrl":"https://depot.example.com/blob/upload/abc"}""")));
        var handler = new _StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        using var httpClient = new HttpClient(handler);
        var bytes = new byte[] { 1, 2, 3 };

        var result = await CockpitProjectLogoBlob.UploadAsync(host, "Depot: Synvolution", "cockpit", bytes, httpClient);

        Assert.Equal(PluginMcpToolCallOutcome.Success, result.Outcome);
        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal("https://depot.example.com/blob/upload/abc", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal(bytes, handler.LastRequestBody);
        Assert.Equal("image/png", handler.LastRequest.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task UploadAsync_RequestUploadCallFails_NeverAttemptsTheHttpPut()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "request_upload", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Failed("no permission")));
        var handler = new _StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);

        var result = await CockpitProjectLogoBlob.UploadAsync(host, "Depot: Synvolution", "cockpit", [1, 2, 3], httpClient);

        Assert.Equal(PluginMcpToolCallOutcome.Failed, result.Outcome);
        Assert.Equal("no permission", result.Error);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task UploadAsync_NotSignedIn_ReportsAuthorizationRequired()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "request_upload", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.AuthorizationRequired));

        var result = await CockpitProjectLogoBlob.UploadAsync(host, "Depot: Synvolution", "cockpit", [1]);

        Assert.Equal(PluginMcpToolCallOutcome.AuthorizationRequired, result.Outcome);
    }

    [Fact]
    public async Task UploadAsync_PutReturnsAnErrorStatus_ReportsFailed()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "request_upload", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success("""{"uploadUrl":"https://depot.example.com/blob/upload/abc"}""")));
        var handler = new _StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var httpClient = new HttpClient(handler);

        var result = await CockpitProjectLogoBlob.UploadAsync(host, "Depot: Synvolution", "cockpit", [1], httpClient);

        Assert.Equal(PluginMcpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("403", result.Error);
    }

    [Fact]
    public async Task UploadAsync_ToolResultMissingUploadUrl_ReportsFailedRatherThanThrowing()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "request_upload", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success("""{"expiresInSeconds":300}""")));
        var handler = new _StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);

        var result = await CockpitProjectLogoBlob.UploadAsync(host, "Depot: Synvolution", "cockpit", [1], httpClient);

        Assert.Equal(PluginMcpToolCallOutcome.Failed, result.Outcome);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task DownloadAsync_Success_CallsRequestDownloadThenGetsTheBytesFromTheReturnedUrl()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "request_download", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success("""{"downloadUrl":"https://depot.example.com/blob/download/xyz"}""")));
        var expectedBytes = new byte[] { 137, 80, 78, 71 };
        var handler = new _StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(expectedBytes) });
        using var httpClient = new HttpClient(handler);

        var result = await CockpitProjectLogoBlob.DownloadAsync(host, "Depot: Synvolution", "cockpit", httpClient);

        Assert.Equal(PluginMcpToolCallOutcome.Success, result.Outcome);
        Assert.Equal(expectedBytes, result.Bytes);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("https://depot.example.com/blob/download/xyz", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task DownloadAsync_NotSignedIn_ReportsAuthorizationRequired()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "request_download", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.AuthorizationRequired));

        var result = await CockpitProjectLogoBlob.DownloadAsync(host, "Depot: Synvolution", "cockpit");

        Assert.Equal(PluginMcpToolCallOutcome.AuthorizationRequired, result.Outcome);
    }

    [Fact]
    public async Task DownloadAsync_ToolCallFails_NeverAttemptsTheHttpGet()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "request_download", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Failed("not found")));
        var handler = new _StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);

        var result = await CockpitProjectLogoBlob.DownloadAsync(host, "Depot: Synvolution", "cockpit", httpClient);

        Assert.Equal(PluginMcpToolCallOutcome.Failed, result.Outcome);
        Assert.Null(handler.LastRequest);
    }
}
