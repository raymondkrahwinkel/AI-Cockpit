using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.BackendApi;
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

    // The HTTPS door: a session started over the backend API answers, and its row reaches the event stream. A key
    // whose scope lacks the profile gets a 404 on the pane and never sees its rows, though it does see the next
    // session's arrival that follows them.
    [Fact]
    public async Task ASessionStartedOverTheApi_StreamsItsAnswer_OnlyToAKeyThatMaySeeIt()
    {
        await using var cockpit = JourneyHost.Api();
        await cockpit.CaptureBootstrapKeyAsync(BootstrapKey);
        var services = cockpit.Services;
        await _SaveAFreshNodeAsync(services);
        cockpit.Backend.Start();
        var nodeUrl = new Uri(Assert.Single(services.GetRequiredService<CockpitMcpEndpointHost>().GetNodeAddresses()).Url);
        var baseAddress = new Uri(nodeUrl, "/");
        var fingerprint = services.GetRequiredService<NodeSelfSignedCertificate>().Fingerprint;
        var issuer = new NodeCaller("bootstrap", "", ConnectKeyCapability.Admin, "127.0.0.1", CancellationToken.None);
        var narrow = await services.GetRequiredService<ConnectKeyVerifier>().IssueAsync(
            "narrow", ConnectKeyCapability.Operate, 30, issuer, scope: new ConnectKeyScope { AllowAllProfiles = false, AllowedProfileLabels = ["Other"] });
        using var admin = new BackendApiClient(baseAddress, BootstrapKey, fingerprint, TimeProvider.System);
        using var outsider = new BackendApiClient(baseAddress, narrow.Secret, fingerprint, TimeProvider.System);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var started = await admin.SendAsync<JsonObject>(HttpMethod.Post, "api/v1/sessions", new { profile = Profile, prompt = "hello" });
        var paneId = started["paneId"]?.GetValue<string>() ?? "";
        var answer = await admin.StreamEventsAsync(0, timeout.Token)
            .FirstAsync(evt => _RowOf(evt) == paneId && evt.Data.GetRawText().Contains("echo: hello", StringComparison.Ordinal), timeout.Token);
        var refused = await Assert.ThrowsAsync<BackendApiException>(() => outsider.GetAsync<JsonObject>($"api/v1/sessions/{paneId}/transcript"));
        // A second start as the marker: its registration is announced to every key, while the first pane is still live.
        await admin.SendAsync<JsonObject>(HttpMethod.Post, "api/v1/sessions", new { profile = Profile });
        var seenByTheOutsider = await outsider.StreamEventsAsync(0, timeout.Token)
            .TakeWhile(evt => evt.Kind != "sessions-changed" || evt.Seq < answer.Seq)
            .ToListAsync(timeout.Token);

        Assert.Equal(HttpStatusCode.NotFound, refused.Status);
        Assert.DoesNotContain(seenByTheOutsider, evt => _RowOf(evt) == paneId);
    }

    // The pane a row event belongs to, from its data: an SSE frame has no pane of its own.
    private static string? _RowOf(Cockpit.Core.Abstractions.Events.BackendEvent evt) =>
        evt.Kind == "row" && evt.Data.TryGetProperty("PaneId", out var pane) ? pane.GetString() : null;

    // AC-1442: the backend's stop without App. Of two sessions one has a CLI that never lets go; within the budget the
    // register is empty, the listeners' ports are free, and that CLI's process is ended rather than left behind.
    [Fact]
    public async Task StoppingTheBackend_EndsBothSessionsWithinTheBudget_AndLeavesNoProcessBehind()
    {
        await using var cockpit = JourneyHost.Api();
        var services = cockpit.Services;
        await _SaveAFreshNodeAsync(services);
        cockpit.Drivers.Next = () => new EchoDriver(wedged: true);
        cockpit.Backend.Start();
        cockpit.Backend.StartPlanners();
        var endpoints = services.GetRequiredService<CockpitMcpEndpointHost>();
        (IPAddress Address, int Port)[] listeners =
        [
            (IPAddress.Any, new Uri(Assert.Single(endpoints.GetNodeAddresses()).Url).Port),
            (IPAddress.Loopback, new Uri(endpoints.GetServers()[0].Url ?? "").Port),
        ];
        var registry = services.GetRequiredService<ISessionRegistry>();
        var launcher = services.GetRequiredService<ISessionLauncher>();
        var profile = Assert.Single(await services.GetRequiredService<ISessionProfileStore>().LoadAsync());
        for (var session = 0; session < 2; session++)
        {
            Assert.NotNull(await launcher.StartSessionAsync(new SessionLaunchRequest(
                launcher.Workspaces.Workspaces[0].Id, profile, null, null, null, PaneSessionKind.Sdk, null, false, null, false)));
        }

        var child = cockpit.Drivers.Made[1].Child ?? throw new InvalidOperationException("The wedged session started no process.");
        try
        {
            var budget = TimeSpan.FromSeconds(4);
            var clock = Stopwatch.StartNew();
            await cockpit.Backend.StopAsync(budget);

            Assert.True(clock.Elapsed < budget, $"The stop took {clock.Elapsed}.");
            Assert.Empty(registry.All);
            Assert.True(child.WaitForExit(TimeSpan.FromSeconds(5)), $"Process {child.Id} outlived the stop.");
            foreach (var (address, port) in listeners)
            {
                var probe = new TcpListener(address, port);
                probe.Start();
                probe.Stop();
            }
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }

            child.Dispose();
        }
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
