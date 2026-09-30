using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Cockpit.Core.Mcp;
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

    /// <summary>
    /// AC-856: the paired controller is not a session on this machine and has no inbox here, so nothing rides back
    /// to it on a tool result. The mail is posted straight into the store rather than through <c>notify</c> — what
    /// keeps this shut today is that <c>notify</c> will not address a pane off the desk, and that is somebody
    /// else's invariant to change. This asserts the boundary that does not depend on it.
    /// </summary>
    [Fact]
    public void Attach_ForThePairedController_CarriesNothing_EvenWithMailSittingInItsInbox()
    {
        McpRequestContext.Set(NodeCallerIdentity.PaneId);
        _inbox.Deliver("pane-b", NodeCallerIdentity.PaneId, "heads-up", "post for the controller");
        var result = _Result();

        var after = McpInboxPiggyback.Attach(result, _Delivery(), NullLogger.Instance);

        Assert.Same(result, after);
        Assert.Single(after.Content);
        Assert.DoesNotContain("post for the controller", _TextOf(after), StringComparison.Ordinal);
    }

    /// <summary>
    /// AC-856, the half that makes the one above safe: withheld is not consumed. Had the skip been written as
    /// "take it and drop it", mail addressed to this pane would vanish with its sender told it was delivered —
    /// the one failure the whole piggyback path is built to avoid.
    /// </summary>
    [Fact]
    public void Attach_ForThePairedController_LeavesThatMailWaiting_RatherThanEatingIt()
    {
        McpRequestContext.Set(NodeCallerIdentity.PaneId);
        _inbox.Deliver("pane-b", NodeCallerIdentity.PaneId, "heads-up", "post for the controller");

        McpInboxPiggyback.Attach(_Result(), _Delivery(), NullLogger.Instance);

        var waiting = Assert.Single(_inbox.Drain(NodeCallerIdentity.PaneId, int.MaxValue).Messages);
        Assert.Equal("post for the controller", waiting.Body);
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
