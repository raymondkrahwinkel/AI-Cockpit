using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Core.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Assistant;
using Cockpit.Core.Swarm;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.BackendApi;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Swarm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Cockpit.Infrastructure.Tests.Swarm;

public sealed class SwarmAssistantRelayTests
{
    [Fact]
    public async Task ChatAndInboxFromAFollowerReachOnlyTheLeaderOnceIncludingAfterALostAcknowledgement()
    {
        await using var pair = await SwarmJoinFixture.CreateAsync();
        var handshake = await pair.A.Client.BeginAsync(pair.B.Address);
        pair.B.Join.Confirm(handshake.Offer.PairingId);
        await pair.A.Client.CompleteAsync(handshake);
        var membership = await pair.A.Membership.LoadAsync();
        var desktop = membership.Members.Single(member => member.Id == pair.A.Identity.MemberId);
        var leader = Substitute.For<ISwarmLeadership>();
        leader.Current.Returns(desktop);
        leader.IsSelf.Returns(true);
        leader.WithLeader(Arg.Any<Func<SwarmMember?, bool>>()).Returns(call => call.Arg<Func<SwarmMember?, bool>>()(leader.Current));
        var follower = Substitute.For<ISwarmLeadership>();
        follower.Current.Returns(desktop);
        follower.IsSelf.Returns(false);
        follower.IsActive.Returns(true);
        var desktopInbox = new AgentMessageInbox();
        using var desktopServices = new ServiceCollection().AddSingleton(pair.A.Membership).AddSingleton(pair.A.Identity)
            .AddSingleton<IAgentMessageInbox>(desktopInbox).BuildServiceProvider();
        using var laptopServices = new ServiceCollection().AddSingleton(pair.B.Membership).AddSingleton(pair.B.Identity)
            .AddSingleton<ISwarmLeadership>(follower).BuildServiceProvider();
        var session = Substitute.For<IAssistantSession>();
        session.IsSessionReady.Returns(true);
        session.CanTakeAPrompt.Returns(true);
        var transcript = new List<TranscriptSnapshotEntry>();
        session.When(value => value.InjectAndSubmit(Arg.Any<string>())).Do(call => transcript.Add(
            new(Guid.NewGuid().ToString("N"), "UserText", call.Arg<string>(), null, null, null, null, false, DateTimeOffset.UtcNow)));
        var desktopRelay = new SwarmAssistantRelay(leader, desktopServices) { Start = _ => Task.FromResult<IAssistantSession?>(session) };
        var laptopRelay = new SwarmAssistantRelay(follower, laptopServices)
        {
            Start = _ => throw new InvalidOperationException("A follower must never start its own assistant."),
        };
        var lost = true;
        var handle = Substitute.For<ISessionHandle>();
        handle.ReadRowsAtAsync(Arg.Any<Func<long>>()).Returns(call => Task.FromResult<SessionRowSnapshot?>(new(transcript.ToArray(), 1)));
        handle.RespondToPermissionByIdAsync("permission", true, false).Returns(true);
        var registry = Substitute.For<ISessionRegistry>();
        registry.Assistant.Returns(handle);
        var log = Substitute.For<IBackendEventLog>();
        var policy = new SwarmNetworkPolicy(pair.A.SwarmSettings,
            new NodeAccessAuditLog(Path.Combine(AppContext.BaseDirectory, $"assistant-relay-{Guid.NewGuid():N}.jsonl"), NullLogger<NodeAccessAuditLog>.Instance),
            NullLogger<SwarmNetworkPolicy>.Instance);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(policy).AddSingleton(pair.A.Membership).AddSingleton(desktopRelay)
            .AddSingleton<ISwarmLeadership>(leader).AddSingleton(registry).AddSingleton(log);
        await using var app = builder.Build();
        var laptop = membership.Members.Single(member => member.Id == pair.B.Identity.MemberId);
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey("X-Member"))
            {
                McpRequestContext.Set(null, new NodeCaller("member", SwarmKick.KeyLabel(laptop), ConnectKeyCapability.Admin, "127.0.0.1", CancellationToken.None));
            }
            try { await next(); }
            finally { McpRequestContext.Set(null); }
        });
        SwarmAssistantRelay.Map(app, app.Services);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        using var anonymous = await http.GetAsync("swarm/assistant/transcript");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        http.DefaultRequestHeaders.Add("X-Member", "1");
        laptopRelay.Transport = async (target, message, ct) =>
        {
            Assert.Equal(desktop.Id, target.Id);
            using var response = await http.PostAsJsonAsync("swarm/assistant", message, ct);
            var accepted = response.StatusCode == HttpStatusCode.OK;
            if (lost) { lost = false; throw new HttpRequestException("Acknowledgement lost"); }
            return accepted;
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var laptopHost = new AssistantSessionHost(Substitute.For<IAssistantSessionFactory>(),
            Substitute.For<INodeControllerPresence>(), Substitute.For<IAssistantSettingsStore>(),
            Substitute.For<IAssistantProfileStore>(), Substitute.For<ISessionStateStore>(),
            new SessionStateRecorder(Substitute.For<ISessionStateStore>(), new SessionConversationTracker(), NullLogger<SessionStateRecorder>.Instance),
            Substitute.For<IMcpServerCatalog>(), Substitute.For<IAssistantMemory>(),
            NullLogger<AssistantSessionHost>.Instance, laptopRelay, follower);
        await laptopHost.SendAsync("Review this", timeout.Token);
        using var backendClient = new BackendApiClient(http, TimeProvider.System, "swarm/session-events");
        var remote = new RemoteSessionHandle(backendClient, AssistantIdentity.PaneId, isAssistant: true);
        Assert.True(await remote.LoadSnapshotAsync(false, timeout.Token));
        Assert.Equal($"[from {laptop.Name}] Review this", Assert.Single(remote.Rows).Text);
        Assert.True(await remote.RespondToPermissionByIdAsync("permission", true));
        await handle.Received(1).RespondToPermissionByIdAsync("permission", true, false);
        session.Received(1).InjectAndSubmit($"[from {membership.Members.Single(member => member.Id == pair.B.Identity.MemberId).Name}] Review this");
        Assert.False(await laptopRelay.ReceiveAsync(pair.A.Identity.MemberId, new(Guid.NewGuid(), "wrong target", []), timeout.Token));

        var laptopInbox = new AgentMessageInbox(services: laptopServices);
        var workspace = Substitute.For<IWorkspaceAgentGateway>();
        workspace.GetWorkspaceSnapshotAsync("agent-laptop").Returns(new WorkspaceAgentSnapshot("desk",
            [new WorkspaceAgentPane("agent-laptop", "Agent", null, "", true)]));
        var tools = new AgentsMcpTools(workspace, new WorkspaceAgentCoordinator(), laptopInbox,
            new AgentNotifyAuditLog(Path.Combine(AppContext.BaseDirectory, $"swarm-notify-{Guid.NewGuid():N}.jsonl"), NullLogger<AgentNotifyAuditLog>.Instance),
            new AgentResourceClaims(), new AgentLineBudget(TimeProvider.System, TimeSpan.FromMinutes(1), 10000, 10000), swarmLeadership: follower);
        McpRequestContext.Set("agent-laptop");
        using var notify = JsonDocument.Parse(await tools.NotifyAsync(AssistantIdentity.PaneId, "done", "Ready"));
        McpRequestContext.Set(null);
        Assert.True(notify.RootElement.GetProperty("ok").GetBoolean());
        var queued = Assert.IsType<AgentMessage>(laptopInbox.PeekOldest(SwarmAssistantRelay.Outbox));
        Assert.Empty(laptopInbox.Drain(AssistantIdentity.PaneId, 1).Messages);
        Assert.Equal(queued.Id, Assert.Single(laptopInbox.TakeForDelivery(SwarmAssistantRelay.Outbox, 1).Messages).Id);
        var envelope = new SwarmAssistantMessage(Guid.Parse(queued.Id), "", [], queued);
        await laptopRelay.SendAsync(envelope, timeout.Token);
        var received = Assert.Single(desktopInbox.Drain(AssistantIdentity.PaneId, 1).Messages);
        Assert.Contains("Ready", received.Body);
        await laptopRelay.SendAsync(envelope, timeout.Token);
        Assert.Empty(desktopInbox.Drain(AssistantIdentity.PaneId, 1).Messages);
        leader.IsSelf.Returns(false);
        Assert.False(await desktopRelay.ReceiveAsync(pair.B.Identity.MemberId, new(Guid.NewGuid(), "waiting", []), timeout.Token));
        follower.Current.Returns((SwarmMember?)null);
        var waiting = laptopHost.SendAsync("Typed during the switch", timeout.Token);
        Assert.False(waiting.IsCompleted);
        follower.Current.Returns(desktop);
        leader.IsSelf.Returns(true);
        await waiting;
        session.Received(1).InjectAndSubmit(Arg.Is<string>(text => text.EndsWith("Typed during the switch", StringComparison.Ordinal)));

        var transportStore = Substitute.For<IMcpServerStore>();
        var incompleteRetried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        transportStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var read = Interlocked.Increment(ref reads);
            if (read == 1)
            {
                return Task.FromResult<IReadOnlyList<McpServerConfig>>([]);
            }
            if (read >= 3)
            {
                incompleteRetried.TrySetResult();
            }
            return Task.FromResult<IReadOnlyList<McpServerConfig>>(
                [new() { Name = NodeServerName.For(desktop.Name, NodeServerName.SessionsServerName) }]);
        });
        using var transportServices = new ServiceCollection().AddSingleton(pair.B.Identity)
            .AddSingleton(transportStore).BuildServiceProvider();
        var joiningRelay = new SwarmAssistantRelay(follower, transportServices);
        using var pendingTransport = new CancellationTokenSource();
        var pendingSend = joiningRelay.SendAsync("Typed before the transport arrived", [], pendingTransport.Token);
        Assert.False(pendingSend.IsCompleted);
        await incompleteRetried.Task.WaitAsync(timeout.Token);
        Assert.False(pendingSend.IsCompleted);
        pendingTransport.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingSend);
    }
}
