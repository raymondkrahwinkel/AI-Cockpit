using System.Net;
using Cockpit.Infrastructure.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// <see cref="OpenAiCompatModelCatalog"/> parses model ids from a local server's <c>/v1/models</c> and
/// degrades to an empty list (never throws) when the server is unreachable or errors.
/// </summary>
public class OpenAiCompatModelCatalogTests
{
    [Fact]
    public async Task ListModelsAsync_ParsesTheModelIdsFromV1Models()
    {
        var handler = new StubHandler("""{"object":"list","data":[{"id":"llama3.1"},{"id":"qwen2.5-7b-instruct"}]}""", HttpStatusCode.OK);
        var catalog = new OpenAiCompatModelCatalog(new HttpClient(handler), NullLogger<OpenAiCompatModelCatalog>.Instance);

        var models = await catalog.ListModelsAsync("http://localhost:11434");

        Assert.Equal(new[] { "llama3.1", "qwen2.5-7b-instruct" }, models);
        Assert.Equal("http://localhost:11434/v1/models", handler.LastRequestUri);
    }

    [Fact]
    public async Task ListModelsAsync_WhenTheServerErrors_ReturnsEmpty()
    {
        var catalog = new OpenAiCompatModelCatalog(
            new HttpClient(new StubHandler(string.Empty, HttpStatusCode.ServiceUnavailable)),
            NullLogger<OpenAiCompatModelCatalog>.Instance);

        Assert.Empty((await catalog.ListModelsAsync("http://localhost:1234")));
    }

    [Fact]
    public async Task ListModelsAsync_WithNoBaseUrl_ReturnsEmpty()
    {
        var catalog = new OpenAiCompatModelCatalog(
            new HttpClient(new StubHandler("{}", HttpStatusCode.OK)), NullLogger<OpenAiCompatModelCatalog>.Instance);

        Assert.Empty((await catalog.ListModelsAsync("")));
    }

    private sealed class StubHandler(string body, HttpStatusCode status) : HttpMessageHandler
    {
        public string? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
