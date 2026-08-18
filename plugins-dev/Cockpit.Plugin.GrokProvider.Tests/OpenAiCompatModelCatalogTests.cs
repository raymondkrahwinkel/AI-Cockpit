using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Cockpit.Plugin.GrokProvider.Tests;

// AC-929: the model listing behind the config view's "Fetch" button — the request it builds (the base URL
// already carries its own version segment, so only `/models` is appended) and the ids it reads back.
public class OpenAiCompatModelCatalogTests
{
    [Fact]
    public async Task ListAsync_AppendsModelsToTheBaseUrlAndSendsTheKeyAsABearerToken()
    {
        var handler = new StubHandler("""{"object":"list","data":[{"id":"grok-4.6"}]}""");
        using var httpClient = new HttpClient(handler);

        await OpenAiCompatModelCatalog.ListAsync(httpClient, "https://api.x.ai/v1/", "super-secret-key", CancellationToken.None);

        Assert.Equal("https://api.x.ai/v1/models", handler.RequestUri?.ToString());
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "super-secret-key"), handler.Authorization);
    }

    [Fact]
    public async Task ListAsync_ReturnsTheIdsSortedAndWithoutBlanks()
    {
        var handler = new StubHandler("""{"data":[{"id":"grok-4.6"},{"id":""},{"id":"grok-3-mini"}]}""");
        using var httpClient = new HttpClient(handler);

        var models = await OpenAiCompatModelCatalog.ListAsync(httpClient, "https://api.x.ai/v1", "key", CancellationToken.None);

        Assert.Equal(["grok-3-mini", "grok-4.6"], models);
    }

    [Fact]
    public async Task ListAsync_ThrowsOnARejectedKeySoTheConfigViewCanSaySo()
    {
        var handler = new StubHandler("unauthorized", HttpStatusCode.Unauthorized);
        using var httpClient = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => OpenAiCompatModelCatalog.ListAsync(httpClient, "https://api.x.ai/v1", "wrong-key", CancellationToken.None));
    }

    private sealed class StubHandler(string body, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public AuthenticationHeaderValue? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
