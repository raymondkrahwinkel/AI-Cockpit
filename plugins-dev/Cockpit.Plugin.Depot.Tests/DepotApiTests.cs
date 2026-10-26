using System.Net;
using System.Net.Http.Headers;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

public class DepotApiTests
{
    private const string Token = "secret-access-token";

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, nameof(DepotApiOutcome.NotSignedIn))]
    [InlineData(HttpStatusCode.Forbidden, nameof(DepotApiOutcome.Forbidden))]
    public async Task GetAsync_MapsSpecialStatusesWithoutExposingAuthorization(HttpStatusCode statusCode, string expected)
    {
        var host = Substitute.For<ICockpitHost>();
        var connection = new DepotConnectionRegistration("c1", "Work", "https://depot.example.com");
        host.CreateAuthorizedHandler(connection.McpServerName).Returns(new _AuthorizedHandler(statusCode));
        using var api = new DepotApi(host, connection);

        var result = await api.GetAsync("cockpit/projects", CancellationToken.None);

        Assert.Equal(expected, result.Outcome.ToString());
        Assert.DoesNotContain("Authorization", result.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Token, result.ToString(), StringComparison.Ordinal);
    }

    private sealed class _AuthorizedHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent($"Authorization: Bearer {Token}"),
                RequestMessage = request,
            });
        }
    }
}
