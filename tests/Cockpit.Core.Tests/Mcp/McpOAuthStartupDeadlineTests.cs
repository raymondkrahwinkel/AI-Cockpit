using System.Diagnostics;
using System.Net;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Tests.Claude;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Mcp;

public class McpOAuthStartupDeadlineTests
{
    [Fact]
    public async Task SessionFanOut_ResolvesInParallel_WithOneBoundedRenewalPerServer()
    {
        using var handler = new HangingHandler();
        using var client = McpOAuthCoordinator.CreateHttpClient(new HangingHandler());
        Assert.InRange(client.Timeout, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(15));
        var store = new FakeMcpOAuthTokenStore();
        var first = _Server("first.example");
        var second = _Server("second.example");
        await store.SaveAsync(first.IdentityKey, first.Name, _ExpiredToken(first.Url));
        await store.SaveAsync(second.IdentityKey, second.Name, _ExpiredToken(second.Url));
        var transports = 0;
        var coordinator = new McpOAuthCoordinator(store, new FakeMcpOAuthAuthorizer(),
            NullLogger<McpOAuthCoordinator>.Instance, httpHandlerFactory: () =>
            {
                Interlocked.Increment(ref transports);
                return handler;
            });
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([first, second]);
        var firstDriver = new FakePluginSessionDriver();
        var secondDriver = new FakePluginSessionDriver();
        var firstAdapter = new PluginSessionDriverAdapter(firstDriver, firstDriver.Capabilities, new McpAuthKey(), catalog,
            oauthCoordinator: coordinator);
        var secondAdapter = new PluginSessionDriverAdapter(secondDriver, secondDriver.Capabilities, new McpAuthKey(), catalog,
            oauthCoordinator: coordinator);

        var elapsed = Stopwatch.StartNew();
        var starts = Task.WhenAll(firstAdapter.StartAsync(), secondAdapter.StartAsync());
        await Task.WhenAll(handler.First.Task, handler.Second.Task).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(starts.IsCompleted);
        await starts.WaitAsync(TimeSpan.FromSeconds(18));

        Assert.InRange(elapsed.Elapsed, TimeSpan.FromSeconds(14), TimeSpan.FromSeconds(18));
        Assert.Equal(2, transports);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<Cockpit.Plugins.Abstractions.Sessions.PluginMcpServer>>(firstDriver.LastMcpServers));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<Cockpit.Plugins.Abstractions.Sessions.PluginMcpServer>>(secondDriver.LastMcpServers));
    }

    private static McpServerConfig _Server(string name) => new()
    {
        Id = name,
        Name = name,
        Transport = McpTransport.Http,
        Url = $"https://{name}/mcp",
        Auth = McpServerAuth.OAuth,
    };

    private static McpOAuthToken _ExpiredToken(string? url) => new()
    {
        AccessToken = "fake-expired",
        RefreshToken = "fake-refresh",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        ResourceUrl = url,
    };

    private sealed class HangingHandler : HttpMessageHandler
    {
        public TaskCompletionSource First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var signal = request.RequestUri?.Host == "first.example" ? First : Second;
            signal.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
