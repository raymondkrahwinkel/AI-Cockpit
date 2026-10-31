using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Swarm;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Swarm;

namespace Cockpit.Infrastructure.Tests.Swarm;

/// <summary>
/// AC-1916 criteria 2 and 3 (layer 2, authorization): an outside address is refused on the way out — also when the
/// same name resolved inside a moment earlier — and on the way in, where the refusal leaves an audit line.
/// </summary>
public sealed class SwarmNetworkPolicyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"swarm-network-{Guid.NewGuid():N}");

    [Fact]
    public async Task AnOutsideAddress_IsRefused_AfterARebindOutward_AndAtTheSwarmDoorInward()
    {
        var answers = new Queue<IPAddress[]>([[IPAddress.Parse("192.168.1.5")], [IPAddress.Parse("8.8.8.8")]]);
        var audit = new NodeAccessAuditLog(Path.Combine(_directory, "node-access.jsonl"), NullLogger<NodeAccessAuditLog>.Instance);
        var policy = new SwarmNetworkPolicy(
            new SwarmSettingsStore(Path.Combine(_directory, "cockpit.json")),
            audit,
            NullLogger<SwarmNetworkPolicy>.Instance,
            (_, _) => Task.FromResult(answers.Dequeue()));

        Assert.Equal([IPAddress.Parse("192.168.1.5")], await policy.ResolveInternalAsync("node.lan"));
        var rebound = await Assert.ThrowsAsync<NodePairingException>(() => policy.ResolveInternalAsync("node.lan"));
        Assert.Equal(SwarmNetworkError.AddressNotInternal, rebound.Error);

        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse("8.8.8.8");
        http.Request.Path = "/swarm/join";
        var result = await policy.InvokeAsync(EndpointFilterInvocationContext.Create(http), _ => ValueTask.FromResult<object?>("reached"));

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<JsonHttpResult<NodePairingProblem>>(result).StatusCode);
        var entry = Assert.Single(await audit.ReadRecentAsync());
        Assert.Equal((SwarmNetworkError.CallerNotInternal, "8.8.8.8"), (entry.Outcome, entry.RemoteAddress));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
