using System.Text.Json;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Swarm;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Swarm;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Swarm;

public sealed class SwarmStandbyCopyTests
{
    [Fact]
    public async Task AHardLeaderFailureRestartsFromTheLastCompleteCopyEvenWhenTheNextTransferBreaks()
    {
        await using var pair = await StandbyAssistant.JoinAsync();
        using var desktop = new StandbyAssistant(pair.A);
        using var laptop = new StandbyAssistant(pair.B);
        await desktop.Host.EnsureLocalStartedAsync(CancellationToken.None);
        var accepted = new SwarmAssistantMessage(Guid.NewGuid(), "Keep working on AC-1928", []);
        Assert.True(await desktop.Relay.ReceiveAsync(pair.B.Identity.MemberId, accepted, CancellationToken.None));
        desktop.Rows.Clear();
        desktop.Rows.Add(new("prompt", "UserText", "Keep working on AC-1928", null, null, null, null, false, DateTimeOffset.UtcNow));
        var mail = new SwarmAssistantMessage(Guid.NewGuid(), "", [], new AgentMessage(Guid.NewGuid().ToString(), "agent", AssistantIdentity.PaneId,
            "review", "Review is waiting", DateTimeOffset.UtcNow));
        Assert.True(await desktop.Relay.ReceiveAsync(pair.B.Identity.MemberId, mail, CancellationToken.None));
        await desktop.Memory.NoteCurrentStateAsync("Review the standby implementation next");
        var first = Assert.IsType<SwarmStandbySnapshot>(pair.B.Standby.Read());
        Assert.Equal("prompt", Assert.Single(first.Transcript).Id);
        Assert.Contains("Review the standby", first.NoteState);

        desktop.Rows.Add(new("reply", "AssistantText", "The first check passed", null, null, null, null, false, DateTimeOffset.UtcNow));
        desktop.Session.StateChanged += Raise.Event<EventHandler<bool>>(desktop.Session, false);
        await desktop.Host.StandbyPublication;
        var complete = Assert.IsType<SwarmStandbySnapshot>(pair.B.Standby.Read());
        Assert.Equal(new[] { "prompt", "reply" }, complete.Transcript.Select(row => row.Id));
        var broken = JsonSerializer.SerializeToUtf8Bytes(complete with { Revision = complete.Revision + 1 }, SwarmStandbyCopy.Json);
        await using var truncated = new MemoryStream(broken[..(broken.Length / 2)]);
        await Assert.ThrowsAsync<JsonException>(() => pair.B.Standby.ReceiveAsync(truncated, pair.A.Identity.MemberId, CancellationToken.None));
        Assert.Equal(complete.Revision, pair.B.Standby.Read()!.Revision);

        pair.A.Sleep = true;
        desktop.Leadership.IsSelf.Returns(false);
        desktop.Leadership.Changed += Raise.Event<EventHandler>(desktop.Leadership, EventArgs.Empty);
        await desktop.Host.LeadershipTransition;
        await desktop.Session.Received(1).DisposeAsync();
        laptop.Leadership.IsSelf.Returns(true);
        var member = (await pair.B.Membership.LoadAsync()).Members.Single(item => item.Id == pair.B.Identity.MemberId);
        laptop.Leadership.Current.Returns(member);
        laptop.Leadership.Changed += Raise.Event<EventHandler>(laptop.Leadership, EventArgs.Empty);
        await laptop.Host.LeadershipTransition;
        Assert.Equal(new[] { "prompt", "reply" }, laptop.Rows.Select(row => row.Id));
        var launch = Assert.IsType<AssistantLaunch>(laptop.Launch);
        Assert.Equal(SessionResume.New, launch.Resume);
        var instruction = string.Join("\n", launch.LaunchOptions.Values);
        Assert.Contains("Keep working on AC-1928", instruction);
        Assert.Contains("The first check passed", instruction);
        Assert.Contains("Review the standby implementation next", instruction);
        Assert.Contains(AssistantStandingInstruction.CurrentStateHeading, instruction);
        laptop.Session.Received(1).AddDivider(Arg.Is<string>(text => text.Contains("up to ")));
        Assert.True(await laptop.Relay.ReceiveAsync(pair.B.Identity.MemberId, accepted, CancellationToken.None));
        laptop.Session.DidNotReceive().InjectAndSubmit(Arg.Any<string>());
        Assert.True(await laptop.Relay.ReceiveAsync(pair.B.Identity.MemberId, mail, CancellationToken.None));
        var unread = Assert.Single(laptop.Inbox.Drain(AssistantIdentity.PaneId, 10).Messages);
        Assert.Contains("Review is waiting", unread.Body);
    }
}

internal sealed class StandbyAssistant : IDisposable
{
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("COCKPIT_SCRATCH") ?? AppContext.BaseDirectory, "standby-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;
    internal ISwarmLeadership Leadership { get; } = Substitute.For<ISwarmLeadership>();
    internal IAssistantSession Session { get; } = Substitute.For<IAssistantSession>();
    internal List<TranscriptSnapshotEntry> Rows { get; } = [];
    internal AssistantLaunch? Launch { get; private set; }
    internal AssistantMemoryFile Memory => (AssistantMemoryFile)_services.GetRequiredService<IAssistantMemory>();
    internal AssistantSessionHost Host => _services.GetRequiredService<AssistantSessionHost>();
    internal SwarmAssistantRelay Relay => _services.GetRequiredService<SwarmAssistantRelay>();
    internal IAgentMessageInbox Inbox => _services.GetRequiredService<IAgentMessageInbox>();

    internal StandbyAssistant(SwarmJoinFixture.Machine machine)
    {
        Directory.CreateDirectory(_root);
        Leadership.IsActive.Returns(true);
        Leadership.IsSelf.Returns(machine.Leadership.IsSelf);
        Leadership.Current.Returns(machine.Leadership.Current);
        Leadership.WithLeader(Arg.Any<Func<SwarmMember?, bool>>()).Returns(call => call.Arg<Func<SwarmMember?, bool>>()(Leadership.Current));
        Session.IsSessionReady.Returns(true);
        Session.CanTakeAPrompt.Returns(true);
        Session.Rows.Returns(_ => Rows.ToArray());
        Session.When(session => session.RestoreStandbyTranscript(Arg.Any<IReadOnlyList<TranscriptSnapshotEntry>>())).Do(call =>
        {
            Rows.Clear(); Rows.AddRange(call.Arg<IReadOnlyList<TranscriptSnapshotEntry>>());
        });
        Session.When(session => session.StartAsync(Arg.Any<AssistantLaunch>())).Do(call => Launch = call.Arg<AssistantLaunch>());
        var factory = Substitute.For<IAssistantSessionFactory>();
        factory.CreateAssistantSession().Returns(Session);
        factory.OnOwnerThreadAsync(Arg.Any<Func<Task<bool>>>()).Returns(call => call.Arg<Func<Task<bool>>>()());
        var settings = Substitute.For<IAssistantSettingsStore>();
        settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new AssistantSettings { IsEnabled = true });
        var profiles = Substitute.For<IAssistantProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(new AssistantProfileSlot(new SessionProfile("test", new PluginProviderConfig("test", "{}"))));
        var state = Substitute.For<ISessionStateStore>();
        var services = new ServiceCollection().AddSingleton(machine.Identity).AddSingleton(machine.Membership)
            .AddSingleton(machine.Standby).AddSingleton(machine.Network).AddSingleton<IMcpServerStore>(machine.Servers)
            .AddSingleton(Leadership).AddSingleton(factory).AddSingleton(settings).AddSingleton(profiles).AddSingleton(state)
            .AddSingleton(Substitute.For<INodeControllerPresence>()).AddSingleton(Substitute.For<IMcpServerCatalog>())
            .AddSingleton(new SessionConversationTracker()).AddLogging()
            .AddSingleton<IAgentMessageInbox>(new AgentMessageInbox())
            .AddSingleton<IAssistantMemory>(provider => new AssistantMemoryFile(Path.Combine(_root, "behaviour.md"), Path.Combine(_root, "state.md"), Path.Combine(_root, "machine.md"), provider))
            .AddSingleton<SessionStateRecorder>().AddSingleton<SwarmTakeoverCoordinator>().AddSingleton<SwarmAssistantRelay>()
            .AddSingleton<AssistantSessionHost>();
        _services = services.BuildServiceProvider();
        _ = Host;
    }

    internal static async Task<SwarmJoinFixture> JoinAsync()
    {
        var pair = await SwarmJoinFixture.CreateAsync();
        var handshake = await pair.A.Client.BeginAsync(pair.B.Address);
        pair.B.Join.Confirm(handshake.Offer.PairingId);
        await pair.A.Client.CompleteAsync(handshake);
        var register = await pair.A.Membership.LoadAsync();
        var first = register.Members.Single(member => member.Id == pair.A.Identity.MemberId);
        var second = register.Members.Single(member => member.Id == pair.B.Identity.MemberId);
        if (first.Rank > second.Rank)
        {
            var record = pair.B.Identity.Sign(SwarmMembership.ToPayload(SwarmLeaderElection.RejoinAtBack(register, pair.B.Identity.MemberId)));
            Assert.Equal(SwarmMembershipVerdict.Accepted, register.Accept(record));
            pair.A.Membership.Save(register);
            var other = await pair.B.Membership.LoadAsync();
            Assert.Equal(SwarmMembershipVerdict.Accepted, other.Accept(record));
            pair.B.Membership.Save(other);
        }
        pair.B.Leadership.Observe(pair.A.Identity.MemberId, pair.A.Identity.MemberId, null);
        pair.B.Reachability.Answer(pair.A.Identity.MemberId, System.Net.HttpStatusCode.OK);
        await pair.B.Leadership.RefreshAsync();
        await pair.A.Leadership.RefreshAsync();
        return pair;
    }

    public void Dispose()
    {
        _services.Dispose();
        Directory.Delete(_root, true);
    }
}
