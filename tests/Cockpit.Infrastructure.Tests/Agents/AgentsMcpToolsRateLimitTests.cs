using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Agents;

/// <summary>
/// The rate limit at the tool layer (AC-396) — AC-119's scenario S10. The store's own behaviour is
/// <c>AgentLineBudgetTests</c>; what is proven here is the part that only exists once the tools charge it: that the
/// cap actually stops a send, that the sender is told why in terms it can act on, that the refusal is on the
/// append-only trail, and — the assertion the whole scenario turns on — that a neighbour who did nothing wrong can
/// still reach anyone while a loud sender sits at its limit.
/// <para>
/// The limits are stated small here rather than taken from the defaults: what has to hold is that the guard rail is
/// charged in the right place, and a test that sent twenty messages to reach the interesting case would be asserting
/// the constant instead of the wiring.
/// </para>
/// </summary>
public sealed class AgentsMcpToolsRateLimitTests : IDisposable
{
    private readonly IWorkspaceAgentGateway _gateway = Substitute.For<IWorkspaceAgentGateway>();
    private readonly WorkspaceAgentCoordinator _coordinator = new();
    private readonly AgentMessageInbox _inbox = new();
    private readonly AgentResourceClaims _claims = new();

    // Two messages and one wake per sender per minute: enough to reach the cap in a test, and the same shape as the
    // shipped numbers, which also allow fewer wakes than messages.
    private readonly AgentLineBudget _budget = new(TimeProvider.System, TimeSpan.FromMinutes(1), 2, 1);

    private readonly string _auditPath = Path.Combine(Path.GetTempPath(), $"agent-rate-audit-{Guid.NewGuid():N}.jsonl");

    private AgentNotifyAuditLog _Audit() => new(_auditPath, NullLogger<AgentNotifyAuditLog>.Instance);

    private AgentsMcpTools _Tools() => new(_gateway, _coordinator, _inbox, _Audit(), _claims, _budget);

    private void _DeskWith(params string[] paneIds)
    {
        var snapshot = new WorkspaceAgentSnapshot(
            "ws-1",
            [.. paneIds.Select(paneId => new WorkspaceAgentPane(paneId, paneId, null, string.Empty, true))]);
        foreach (var paneId in paneIds)
        {
            _gateway.GetWorkspaceSnapshotAsync(paneId).Returns(Task.FromResult<WorkspaceAgentSnapshot?>(snapshot));
        }
    }

    private static JsonNode _Json(string result) => JsonNode.Parse(result)!;

    private async Task<JsonNode> _NotifyAs(string caller, string toPaneId, string body, bool urgent = false)
    {
        McpRequestContext.Set(caller);
        // A different body each time by default, so nothing here is carried by de-duplication rather than by the cap.
        return _Json(await _Tools().NotifyAsync(toPaneId, "heads-up", body, urgent));
    }

    private IReadOnlyList<AgentMessage> _Waiting(string paneId) => _inbox.Drain(paneId, int.MaxValue).Messages;

    public void Dispose()
    {
        McpRequestContext.Set(null);
        if (File.Exists(_auditPath))
        {
            File.Delete(_auditPath);
        }
    }

    [Fact]
    public async Task Notify_PastTheMessageLimit_IsRefusedAndSaysHowLongToWait()
    {
        _DeskWith("loud", "target");

        Assert.True((await _NotifyAs("loud", "target", "one"))["ok"]!.GetValue<bool>());
        Assert.True((await _NotifyAs("loud", "target", "two"))["ok"]!.GetValue<bool>());

        var refused = await _NotifyAs("loud", "target", "three");

        Assert.False(refused["ok"]!.GetValue<bool>());
        var error = refused["error"]!.GetValue<string>();
        // The three things the refusal has to carry: what happened, that it lifts on its own, and that it is about
        // this sender alone. A refusal an agent reads as "the line is down" is one it stops using.
        Assert.Contains("seconds and send it again", error, StringComparison.Ordinal);
        Assert.Contains("counts your sends only", error, StringComparison.Ordinal);
    }

    /// <summary>A refused send puts nothing anywhere — the cap is charged before the delivery, not cleaned up after it.</summary>
    [Fact]
    public async Task Notify_PastTheMessageLimit_DeliversNothing()
    {
        _DeskWith("loud", "target");

        await _NotifyAs("loud", "target", "one");
        await _NotifyAs("loud", "target", "two");
        await _NotifyAs("loud", "target", "three");
        await _NotifyAs("loud", "target", "four");

        Assert.Equal(2, _Waiting("target").Count);
    }

    /// <summary>
    /// AC-119 scenario S10's second assertion, and the reason this cap counts sends rather than arrivals: while one
    /// pane sits at its limit, an uninvolved neighbour reaches the same recipient normally. A limit that only bounded
    /// a recipient's inbox would refuse this send too — for something its sender did not do.
    /// </summary>
    [Fact]
    public async Task Notify_WhileOneSenderIsAtItsLimit_ANeighbourStillGetsThrough()
    {
        _DeskWith("loud", "quiet", "target");

        await _NotifyAs("loud", "target", "one");
        await _NotifyAs("loud", "target", "two");
        Assert.False((await _NotifyAs("loud", "target", "three"))["ok"]!.GetValue<bool>());

        var neighbour = await _NotifyAs("quiet", "target", "unrelated, and on time");

        Assert.True(neighbour["ok"]!.GetValue<bool>());
        Assert.Contains(_Waiting("target"), message => message.FromPaneId == "quiet");
    }

    /// <summary>
    /// A refusal the host reached on its own account is not the sender's quota to spend. Without this a couple of
    /// mistyped pane ids would use up the budget an agent needs for the message it got right — and the sender would
    /// be told to slow down for something that never reached anyone.
    /// </summary>
    [Fact]
    public async Task Notify_RefusedBeforeTheCap_DoesNotSpendTheSendersBudget()
    {
        _DeskWith("sender", "target");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var refused = await _NotifyAs("sender", "no-such-pane", $"attempt {attempt}");
            Assert.False(refused["ok"]!.GetValue<bool>());
        }

        // Still the full allowance, because nothing above was ever delivered.
        Assert.True((await _NotifyAs("sender", "target", "one"))["ok"]!.GetValue<bool>());
        Assert.True((await _NotifyAs("sender", "target", "two"))["ok"]!.GetValue<bool>());
    }

    /// <summary>The trail is where the operator sees a sender that kept hitting the rail; the sender is told, but the sender is not who the record is for.</summary>
    [Fact]
    public async Task Notify_PastTheMessageLimit_IsOnTheTrailAsRateLimited()
    {
        _DeskWith("loud", "target");

        await _NotifyAs("loud", "target", "one");
        await _NotifyAs("loud", "target", "two");
        await _NotifyAs("loud", "target", "three");

        var entries = await _Audit().ReadRecentAsync();
        var refused = Assert.Single(entries, entry => entry.Outcome == AgentNotifyOutcome.RefusedRateLimited);
        Assert.Equal("loud", refused.FromPaneId);
        Assert.Equal("target", refused.ToPaneId);
        Assert.Null(refused.MessageId);
    }

    /// <summary>
    /// The wake allowance is spent separately and is scarcer. What matters at this layer is that hitting it costs the
    /// message nothing: the message is delivered and waiting, and only the turn did not happen.
    /// </summary>
    [Fact]
    public async Task Notify_PastTheWakeLimit_StillDeliversButStartsNoTurn()
    {
        _DeskWith("sender", "target");
        _coordinator.SetWakeConsent("target", true);
        _gateway.TryWakeAsync("sender", "target", Arg.Any<string>()).Returns(AgentWakeOutcome.Woken);

        var first = await _NotifyAs("sender", "target", "wake one", urgent: true);
        Assert.True(first["wake"]!["woken"]!.GetValue<bool>());

        var second = await _NotifyAs("sender", "target", "wake two", urgent: true);

        Assert.True(second["ok"]!.GetValue<bool>());
        Assert.False(second["wake"]!["woken"]!.GetValue<bool>());
        Assert.Equal(nameof(AgentWakeOutcome.RateLimited), second["wake"]!["outcome"]!.GetValue<string>());
        // The message itself went through: the wake cap is not a message cap wearing another name.
        Assert.Equal(2, _Waiting("target").Count);
    }

    /// <summary>
    /// A wake refused by the rate limit reaches the gateway not at all — the same shape as the consent refusal. If it
    /// did reach it, the turn would already have been started by the time anything said no.
    /// </summary>
    [Fact]
    public async Task Notify_PastTheWakeLimit_DoesNotReachTheGateway()
    {
        _DeskWith("sender", "target");
        _coordinator.SetWakeConsent("target", true);
        _gateway.TryWakeAsync("sender", "target", Arg.Any<string>()).Returns(AgentWakeOutcome.Woken);

        await _NotifyAs("sender", "target", "wake one", urgent: true);
        _gateway.ClearReceivedCalls();

        await _NotifyAs("sender", "target", "wake two", urgent: true);

        await _gateway.DidNotReceiveWithAnyArgs().TryWakeAsync(default!, default!, default!);
    }

    /// <summary>
    /// A wake refused for going too fast is on the trail with everything else about that attempt. The sender being
    /// told is not enough: an operator asking why an agent kept trying to start turns on a neighbour has only this.
    /// </summary>
    [Fact]
    public async Task Notify_PastTheWakeLimit_IsOnTheTrailWithTheMessageAccepted()
    {
        _DeskWith("sender", "target");
        _coordinator.SetWakeConsent("target", true);
        _gateway.TryWakeAsync("sender", "target", Arg.Any<string>()).Returns(AgentWakeOutcome.Woken);

        await _NotifyAs("sender", "target", "wake one", urgent: true);
        await _NotifyAs("sender", "target", "wake two", urgent: true);

        var entries = await _Audit().ReadRecentAsync();
        var rateLimited = Assert.Single(entries, entry => entry.Wake == AgentWakeOutcome.RateLimited);
        Assert.Equal(AgentNotifyOutcome.Accepted, rateLimited.Outcome);
        Assert.True(rateLimited.Urgent);
        Assert.NotNull(rateLimited.MessageId);
    }
}
