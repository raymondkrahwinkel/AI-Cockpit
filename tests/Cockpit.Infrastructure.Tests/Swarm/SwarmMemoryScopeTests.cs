using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Swarm;

namespace Cockpit.Infrastructure.Tests.Swarm;

public sealed class SwarmMemoryScopeTests
{
    [Fact]
    public async Task StandbyCarriesBehaviourWithoutTheLeadersMachineMemory()
    {
        await using var pair = await StandbyAssistant.JoinAsync();
        using var desktop = new StandbyAssistant(pair.A);
        await desktop.Host.EnsureLocalStartedAsync(CancellationToken.None);
        await desktop.Memory.RememberAsync("Desktop has a machine-only credential", AssistantMemoryScope.Machine);
        await desktop.Memory.RememberAsync("Answer briefly", AssistantMemoryScope.Behaviour);
        var copy = Assert.IsType<SwarmStandbySnapshot>(pair.B.Standby.Read());
        Assert.Contains("Answer briefly", copy.Behaviour);
        Assert.DoesNotContain("machine-only credential", JsonSerializer.Serialize(copy));
        using var client = new HttpClient(NodeCertificatePin.Require(pair.B.Certificate.Fingerprint)) { BaseAddress = new Uri(pair.B.Address) };
        using var anonymous = await client.PostAsJsonAsync("swarm/standby", copy);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var ordinary = await pair.B.Keys.IssueAsync("ordinary-admin", ConnectKeyCapability.Admin, null,
            new NodeCaller(null, "local", ConnectKeyCapability.Admin, "local", CancellationToken.None));
        client.DefaultRequestHeaders.Authorization = new("Bearer", ordinary.Secret);
        using var nonMember = await client.PostAsJsonAsync("swarm/standby", copy);
        Assert.Equal(HttpStatusCode.Forbidden, nonMember.StatusCode);
        var server = Assert.Single(await pair.A.Servers.LoadAsync());
        client.DefaultRequestHeaders.Authorization = new("Bearer", server.ApiKey);
        pair.B.External = true;
        using var external = await client.PostAsJsonAsync("swarm/standby", copy);
        Assert.Equal(HttpStatusCode.Forbidden, external.StatusCode);
        pair.B.External = false;
        Assert.Equal(copy.Revision, pair.B.Standby.Read()!.Revision);
        using var laptop = new StandbyAssistant(pair.B);
        await laptop.Memory.RememberAsync("Laptop has its own paths", AssistantMemoryScope.Machine);
        laptop.Memory.RestoreStandby(copy.NoteState, copy.Behaviour);
        Assert.Contains("Answer briefly", await laptop.Memory.ReadAsync(AssistantMemoryScope.Behaviour));
        Assert.Contains("Laptop has its own paths", await laptop.Memory.ReadAsync(AssistantMemoryScope.Machine));
        Assert.DoesNotContain("machine-only credential", await laptop.Memory.ReadAsync(AssistantMemoryScope.Machine));
    }
}
