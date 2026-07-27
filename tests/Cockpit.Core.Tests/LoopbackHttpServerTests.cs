using System.Net.NetworkInformation;
using FluentAssertions;
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

        _ListeningPorts().Should().Contain(new Uri(server.BaseUrl).Port);

        using var client = new HttpClient();
        var body = await client.GetStringAsync(server.BaseUrl);

        body.Should().Be("served");
    }

    [Fact]
    public async Task DisposeAsync_ReleasesThePort()
    {
        var server = await LoopbackHttpServer.StartAsync(context => context.Response.WriteAsync("served"));
        var port = new Uri(server.BaseUrl).Port;

        await server.DisposeAsync();

        _ListeningPorts().Should().NotContain(port);
    }

    private static IEnumerable<int> _ListeningPorts() =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(endpoint => endpoint.Port);
}
