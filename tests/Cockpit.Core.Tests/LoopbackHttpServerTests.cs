using System.Net.NetworkInformation;
using Cockpit.TestSupport;
using Microsoft.AspNetCore.Http;

namespace Cockpit.Core.Tests;

/// <summary>
/// The one thing <see cref="LoopbackHttpServer"/> exists to guarantee (AC-350): the port it reports is a port it
/// already holds. A fixture that picks a number first and binds it later can have that number taken from under it.
/// </summary>
public class LoopbackHttpServerTests
{
    [Fact]
    public async Task StartAsync_HoldsThePortItReports()
    {
        await using var server = await LoopbackHttpServer.StartAsync(context => context.Response.WriteAsync("served"));

        Assert.Contains(new Uri(server.BaseUrl).Port, _ListeningPorts());

        using var client = new HttpClient();
        var body = await client.GetStringAsync(server.BaseUrl);

        Assert.Equal("served", body);
    }

    [Fact]
    public async Task DisposeAsync_ReleasesThePort()
    {
        var server = await LoopbackHttpServer.StartAsync(context => context.Response.WriteAsync("served"));
        var port = new Uri(server.BaseUrl).Port;

        await server.DisposeAsync();

        Assert.DoesNotContain(port, _ListeningPorts());
    }

    private static IEnumerable<int> _ListeningPorts() =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(endpoint => endpoint.Port);
}
