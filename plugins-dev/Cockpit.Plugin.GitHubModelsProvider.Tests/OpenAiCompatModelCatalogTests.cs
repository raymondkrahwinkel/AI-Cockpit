using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Cockpit.Plugin.GitHubModelsProvider.Tests;

// AC-931: the model listing behind the config view's "Fetch" button — the request it builds (the base URL
// already carries its own version segment, so only `/models` is appended) and the ids it reads back.
public class OpenAiCompatModelCatalogTests
{
    [Fact]
    public async Task ListAsync_AppendsModelsToTheBaseUrlAndSendsTheKeyAsABearerToken()
    {
        var handler = new StubHandler("""{"object":"list","data":[{"id":"openai/gpt-4.1"}]}""");
        using var httpClient = new HttpClient(handler);

        await OpenAiCompatModelCatalog.ListAsync(httpClient, "https://models.github.ai/inference/", "super-secret-token", CancellationToken.None);

        Assert.Equal("https://models.github.ai/inference/models", handler.RequestUri?.ToString());
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "super-secret-token"), handler.Authorization);
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
