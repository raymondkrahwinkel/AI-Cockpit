using System.Net;
using Cockpit.Core.Mcp;
using Cockpit.Core.Swarm;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Swarm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Tests.Swarm;

public sealed class SwarmManualAddressTests
{
    [Theory]
    [InlineData("[fd00::5]:20382", "fd00::5", 20382, true)]
    [InlineData("10.0.0.5", "10.0.0.5", 20382, true)]
    [InlineData("nuc.local:20382", "nuc.local", 20382, false)]
    public async Task ManualAddress_ResolvesAndAuthorizesEveryConnection(string address, string host, int port, bool allowed)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"swarm-manual-{Guid.NewGuid():N}");
        try
        {
            var store = new SwarmSettingsStore(Path.Combine(directory, "cockpit.json"));
            var answers = new Queue<string>(["10.0.0.5", "10.0.0.6", "8.8.8.8"]);
            var policy = new SwarmNetworkPolicy(store,
                new NodeAccessAuditLog(Path.Combine(directory, "audit.jsonl"), NullLogger<NodeAccessAuditLog>.Instance),
                NullLogger<SwarmNetworkPolicy>.Instance,
                (name, _) => Task.FromResult(new[] { IPAddress.Parse(name == "nuc.local" ? answers.Dequeue() : name) }));
            var manual = new SwarmManualAddress(store, policy);
            var memberId = SwarmMemberId.FromPublicKey([1, 2, 3]);
            await manual.RememberAsync(address, memberId.Value, online: true);
            for (var connection = 0; connection < 2; connection++)
            {
                var stored = await manual.ForMemberAsync(memberId);
                Assert.NotNull(stored);
                var uri = await manual.ResolveAsync(stored.ToString());
                Assert.Equal((host, port), (uri.IdnHost, uri.Port));
                await manual.RememberAsync(stored.ToString(), memberId.Value, online: true);
                Assert.Equal("Online", Assert.Single((await store.LoadAsync()).ManualAddresses).Status);
            }
            if (allowed)
            {
                await manual.RememberAsync(address, failure: new TaskCanceledException());
            }
            else
            {
                var error = await Assert.ThrowsAsync<NodePairingException>(() => manual.ResolveAsync(address));
                Assert.Equal(SwarmNetworkError.AddressNotInternal, error.Error);
                await manual.RememberAsync(address, failure: error);
            }
            var saved = Assert.Single((await new SwarmSettingsStore(Path.Combine(directory, "cockpit.json")).LoadAsync()).ManualAddresses);
            Assert.Equal(SwarmManualAddress.Parse(address).Authority, saved.Address);
            Assert.Equal(memberId.Value, saved.MemberId);
            Assert.Equal(allowed ? "No answer" : "Not reachable", saved.Status);
            Assert.Equal(allowed ? "no_answer" : SwarmNetworkError.AddressNotInternal, saved.ErrorCode);
            Assert.NotEmpty(saved.ErrorMessage);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
