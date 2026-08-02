using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Tests.Mcp;

/// <summary>
/// The piggyback route (AC-527): waiting mail attached to the result of a tool call the agent made itself. This is
/// the layer that has to work for every provider and both transports, so what it must never do matters as much as
/// what it does — the acceptance criteria are mostly about the empty case and about not delivering twice.
/// <para>
/// Driven directly rather than through a live MCP server: the filter registration in <c>CockpitMcpEndpointHost</c> is
/// one line that wraps the handler, and everything that could be wrong lives here, in what gets attached and what
/// happens to the inbox when it does.
/// </para>
/// </summary>
public sealed class McpInboxPiggybackTests : IDisposable
{
    private readonly AgentMessageInbox _inbox = new();
    private readonly WorkspaceAgentCoordinator _coordinator = new();

    private AgentTurnInboxDelivery _Delivery() => new(_inbox, _coordinator);

    private static CallToolResult _Result(string text = "the tool's own answer") =>
        new() { Content = [new TextContentBlock { Text = text }] };

    private static string _TextOf(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    public void Dispose() => McpRequestContext.Set(null);

    /// <summary>
    /// Acceptance criterion 2, and the promise the whole form rests on: no mail, not one added character. Compared
    /// byte for byte rather than approximately — the cost argument that chose this shape over a noticeboard is only
    /// worth anything if the empty case is exactly free.
    /// </summary>
    [Fact]
    public void Attach_WithAnEmptyInbox_ReturnsTheResultUntouched()
    {
        McpRequestContext.Set("pane-a");
        var result = _Result();
        var before = _TextOf(result);

        var after = McpInboxPiggyback.Attach(result, _Delivery(), NullLogger.Instance);

        Assert.Same(result, after);
        Assert.Single(after.Content);
        Assert.Equal(before, _TextOf(after));
    }

    [Fact]
    public void Attach_WithMailWaiting_AddsOneBlockCarryingTheSenderAndTheTrustStatement()
    {
        McpRequestContext.Set("pane-a");
        _inbox.Deliver("pane-b", "pane-a", "heads-up", "I am merging DEP-85 to dev");

        var after = McpInboxPiggyback.Attach(_Result(), _Delivery(), NullLogger.Instance);

        Assert.Equal(2, after.Content.Count);
        var text = _TextOf(after);
        // The tool's own answer is still there, first — the block is an addition, not a replacement.
        Assert.StartsWith("the tool's own answer", text, StringComparison.Ordinal);
        Assert.Contains("I am merging DEP-85 to dev", text, StringComparison.Ordinal);
        Assert.Contains("pane-b", text, StringComparison.Ordinal);
        // The framing does not soften by route: the same trust statement as the turn-start notice and read_inbox.
        Assert.Contains(AgentInboxTurnNotice.TrustStatement, text, StringComparison.Ordinal);
        // ...and the one clause that does differ says which way it came.
        Assert.Contains("attached them to the result of the tool call you just made", text, StringComparison.Ordinal);
    }

    /// <summary>Acceptance criterion 3: delivered mail is read mail, and does not come back on the next tool call.</summary>
    [Fact]
    public void Attach_Twice_DeliversTheMessageOnlyOnce()
    {
        McpRequestContext.Set("pane-a");
        _inbox.Deliver("pane-b", "pane-a", "heads-up", "I am merging DEP-85 to dev");
        var delivery = _Delivery();

        McpInboxPiggyback.Attach(_Result(), delivery, NullLogger.Instance);
        var second = McpInboxPiggyback.Attach(_Result(), delivery, NullLogger.Instance);

        Assert.Single(second.Content);
        Assert.DoesNotContain("DEP-85", _TextOf(second), StringComparison.Ordinal);
    }

    /// <summary>
    /// Acceptance criterion 4. Both routes go through the same in-flight split, so a pane that has turn-start
    /// delivery and makes a tool call is handed the message once — whichever gets there first — rather than once per
    /// route. Tested from the other side too: what the piggyback took is not still waiting for a turn.
    /// </summary>
    [Fact]
    public void Attach_ThenTurnStartDelivery_FindsNothingLeftToCarry()
    {
        McpRequestContext.Set("pane-a");
        _inbox.Deliver("pane-b", "pane-a", "heads-up", "I am merging DEP-85 to dev");
        var delivery = _Delivery();

        McpInboxPiggyback.Attach(_Result(), delivery, NullLogger.Instance);

        Assert.Null(delivery.TakeForTurn("pane-a"));
    }

    [Fact]
    public void Attach_AfterTurnStartDeliveryTookTheBatch_AddsNothing()
    {
        McpRequestContext.Set("pane-a");
        _inbox.Deliver("pane-b", "pane-a", "heads-up", "I am merging DEP-85 to dev");
        var delivery = _Delivery();

        var notice = delivery.TakeForTurn("pane-a");
        Assert.NotNull(notice);

        var after = McpInboxPiggyback.Attach(_Result(), delivery, NullLogger.Instance);

        Assert.Single(after.Content);
    }

    /// <summary>
    /// A request the transport could not attribute to a pane has no inbox to read. Refused rather than guessed at,
    /// the same way every tool on this line refuses one — the in-process tool loop and the shared app-key path both
    /// arrive here.
    /// </summary>
    [Fact]
    public void Attach_WithNoVerifiedPane_ReturnsTheResultUntouchedAndLeavesEveryInboxAlone()
    {
        McpRequestContext.Set(null);
        _inbox.Deliver("pane-b", "pane-a", "heads-up", "I am merging DEP-85 to dev");

        var after = McpInboxPiggyback.Attach(_Result(), _Delivery(), NullLogger.Instance);

        Assert.Single(after.Content);
        Assert.Single(_inbox.Drain("pane-a", int.MaxValue).Messages);
    }

    /// <summary>Nothing registered to deliver with is not a failure, it is a host without the agent line wired up.</summary>
    [Fact]
    public void Attach_WithNoDeliveryService_ReturnsTheResultUntouched()
    {
        McpRequestContext.Set("pane-a");
        var result = _Result();

        Assert.Same(result, McpInboxPiggyback.Attach(result, delivery: null, NullLogger.Instance));
    }

    /// <summary>
    /// The failure that must not lose mail: the batch was taken and then something went wrong before it reached the
    /// agent. It goes back to waiting rather than disappearing with its sender told it arrived — the one guarantee
    /// the in-flight split exists for. Provoked with a result whose content list refuses to be read.
    /// </summary>
    [Fact]
    public void Attach_WhenAttachingThrows_PutsTheMailBackAndReturnsTheResult()
    {
        McpRequestContext.Set("pane-a");
        _inbox.Deliver("pane-b", "pane-a", "heads-up", "I am merging DEP-85 to dev");
        var result = new CallToolResult { Content = new ThrowingContentList() };

        var after = McpInboxPiggyback.Attach(result, _Delivery(), NullLogger.Instance);

        Assert.Same(result, after);
        // Still waiting, and still the same message — not dropped, not duplicated.
        var waiting = Assert.Single(_inbox.Drain("pane-a", int.MaxValue).Messages);
        Assert.Equal("I am merging DEP-85 to dev", waiting.Body);
    }

    /// <summary>A content list that cannot be enumerated, so building the new list throws where the attach happens.</summary>
    private sealed class ThrowingContentList : IList<ContentBlock>
    {
        public IEnumerator<ContentBlock> GetEnumerator() => throw new InvalidOperationException("This result refuses to be copied.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public int Count => throw new InvalidOperationException("This result refuses to be counted.");

        public bool IsReadOnly => true;

        public ContentBlock this[int index]
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Add(ContentBlock item) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public bool Contains(ContentBlock item) => throw new NotSupportedException();

        public void CopyTo(ContentBlock[] array, int arrayIndex) => throw new NotSupportedException();

        public int IndexOf(ContentBlock item) => throw new NotSupportedException();

        public void Insert(int index, ContentBlock item) => throw new NotSupportedException();

        public bool Remove(ContentBlock item) => throw new NotSupportedException();

        public void RemoveAt(int index) => throw new NotSupportedException();
    }
}
