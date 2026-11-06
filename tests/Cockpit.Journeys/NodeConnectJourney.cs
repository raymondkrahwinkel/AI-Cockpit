using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Journeys;

// J6, node-connect with a key: AC-1381's and AC-1386's end-to-end facts, one per door, moved here from the backend
// suite. The backend on a fresh state root, with no App and no plugins; only the provider is fake.
[Collection(JourneyCollection.Alone)]
public sealed class NodeConnectJourney
{
    private const string Profile = "Echo";

    // Long and varied enough for the verifier's bootstrap rule; a random hex key can miss one of its 16 digits.
    private const string BootstrapKey = "ck_bootstrapKeyForTheBackendWithoutAppTest0123456789";

    private static readonly string[] CoreEndpoints =
    [
        "cockpit-session", "cockpit-verify", "cockpit-agents", "cockpit-assistant", "cockpit-assistant-agents",
        "cockpit-node", "cockpit-worktrees", "cockpit-terminal", "cockpit-shell",
    ];

    // The MCP door: every core endpoint mounts, and `start_node_agent` with a connect key starts an SDK session whose
    // answer `read_node_transcript` returns.
    [Fact]
    public async Task TheBackendWithoutApp_MountsEveryCoreEndpoint_AndStartsANodeAgentThroughTheConnectKeyDoor()
    {
        await using var cockpit = JourneyHost.Api();
        await cockpit.CaptureBootstrapKeyAsync(BootstrapKey);
        var services = cockpit.Services;
        await _SaveAFreshNodeAsync(services);

        cockpit.Backend.Start();
        cockpit.Backend.StartPlanners();
        var host = services.GetRequiredService<CockpitMcpEndpointHost>();

        // First, so an endpoint that did not mount is named rather than showing up as a node door that is missing.
        Assert.Empty(CoreEndpoints.Except(host.GetServers().Select(server => server.Name)));
        Assert.DoesNotContain("Could not start cockpit MCP endpoint", cockpit.LogText, StringComparison.Ordinal);

        var nodeUrl = Assert.Single(host.GetNodeAddresses()).Url;
        await using var client = await McpClient.CreateAsync(NodeCertificatePin.TransportFor(
            new McpServerConfig { Name = "node", Transport = McpTransport.Http, Url = nodeUrl, PinnedCertificateFingerprint = services.GetRequiredService<NodeSelfSignedCertificate>().Fingerprint },
            new HttpClientTransportOptions { Endpoint = new Uri(nodeUrl), AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {BootstrapKey}" } }));
        var started = await JourneyHost.CallAsync(client, "start_node_agent", new() { ["profile"] = Profile, ["prompt"] = "hello" });
        await cockpit.Driver.Answered.WaitAsync(TimeSpan.FromSeconds(30));
        var transcript = await JourneyHost.CallAsync(client, "read_node_transcript", new() { ["paneId"] = started["paneId"]?.GetValue<string>() });

        Assert.True(started["ok"]?.GetValue<bool>());
        Assert.Contains(
            "AssistantText: echo: hello",
            (transcript["entries"]?.AsArray() ?? []).Select(entry => $"{entry?["kind"]?.GetValue<string>()}: {entry?["text"]?.GetValue<string>()}"));

        // AC-1379's threading edge, measured: a node call reaches the session on a thread with no synchronization
        // context to return to.
        Assert.Null(cockpit.Driver.ContextAtSend);
    }

    // A desk to land on, an SDK profile to run (a TTY one needs a window) and the node door on a port of the OS's choosing.
    private static async Task _SaveAFreshNodeAsync(IServiceProvider services)
    {
        var desk = Workspace.Create("Sessions", WorkspaceType.Sessions);
        await services.GetRequiredService<IWorkspaceSettingsStore>().SaveAsync(new WorkspaceSettings { Workspaces = [desk], ActiveWorkspaceId = desk.Id });
        await services.GetRequiredService<ISessionProfileStore>().SaveAsync([new SessionProfile(Profile, new ClaudeConfig("/fake/.claude")) { DefaultKind = ProfileSessionKind.Sdk }]);
        await services.GetRequiredService<INodeEndpointSettingsStore>().SaveAsync(new NodeEndpointSettings { Enabled = true, SharedSecret = Guid.NewGuid().ToString("N"), Port = 0 });
    }
}
