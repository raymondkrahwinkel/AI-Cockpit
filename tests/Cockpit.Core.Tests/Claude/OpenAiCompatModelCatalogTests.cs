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

    public static TheoryData<Func<HttpClient>, string, bool> ProbeCases => new()
    {
        { () => new HttpClient(new StubHandler("", HttpStatusCode.OK)), "http://localhost:11434", true },
        { () => new HttpClient(), "http://127.0.0.1:1", false },
        { () => new HttpClient(new StubHandler("", HttpStatusCode.InternalServerError)), "http://localhost:11434", false },
        { () => new HttpClient(), "localhost:11434", false },
    };

    [Theory]
    [MemberData(nameof(ProbeCases))]
    public async Task ProbeAsync_ReportsWhetherV1ModelsIsReachable(Func<HttpClient> clientFactory, string baseUrl, bool expected)
    {
        using var client = clientFactory();
        var catalog = new OpenAiCompatModelCatalog(client, NullLogger<OpenAiCompatModelCatalog>.Instance);

        var reachable = await catalog.ProbeAsync(baseUrl, null, TimeSpan.FromSeconds(1));

        Assert.Equal(expected, reachable);
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
