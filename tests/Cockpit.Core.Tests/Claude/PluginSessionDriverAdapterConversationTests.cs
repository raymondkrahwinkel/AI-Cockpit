using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// <see cref="PluginSessionDriverAdapter"/>'s conversation-id reporting (AC-408) — the SDK route half of the one
/// seam both routes report a session's conversation id through. The adapter watches
/// <see cref="IPluginSessionDriver.Conversation"/> as its own event stream is drained and reports a change to the
/// <see cref="ISessionConversationSink"/>, but only when the value actually changed since the last report.
/// </summary>
public class PluginSessionDriverAdapterConversationTests
{
    private static readonly McpAuthKey _authKey = new();

    private static readonly IReadOnlyDictionary<string, string> _PaneOneLaunchOptions =
        new Dictionary<string, string> { [WellKnownPluginSessionOptions.PaneId] = "pane-1" };

    [Fact]
    public async Task Events_ReportsTheKnownConversationId_OnceTheInnerDriverHasOne()
    {
        var inner = new FakePluginSessionDriver { SessionId = "session-a" };
        var sink = Substitute.For<ISessionConversationSink>();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey, conversationSink: sink);
        await adapter.StartAsync(launchOptions: _PaneOneLaunchOptions);

        inner.Emit(_TurnCompleted("session-a"));
        inner.Complete();
        await _DrainAsync(adapter);

        sink.Received(1).Report("pane-1", SessionConversationId.Known("session-a"));
    }

    [Fact]
    public async Task Events_DoesNotReReport_WhenTheConversationIdIsUnchangedAcrossMultipleEvents()
    {
        var inner = new FakePluginSessionDriver { SessionId = "session-a" };
        var sink = Substitute.For<ISessionConversationSink>();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey, conversationSink: sink);
        await adapter.StartAsync(launchOptions: _PaneOneLaunchOptions);

        inner.Emit(_TurnCompleted("session-a"));
        inner.Emit(_TurnCompleted("session-a"));
        inner.Emit(_TurnCompleted("session-a"));
        inner.Complete();
        await _DrainAsync(adapter);

        sink.Received(1).Report("pane-1", SessionConversationId.Known("session-a"));
    }

    [Fact]
    public async Task Events_ReportsAgain_WhenTheConversationIdChangesMidSession()
    {
        var inner = new FakePluginSessionDriver { SessionId = "session-a" };
        var sink = Substitute.For<ISessionConversationSink>();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey, conversationSink: sink);
        await adapter.StartAsync(launchOptions: _PaneOneLaunchOptions);

        // Consumed one event at a time (rather than emitting both up front and draining once) so the SessionId
        // mutation below lands strictly after the first event was already observed and reported — the same
        // ordering a real driver's own state change has relative to its event stream.
        inner.Emit(_TurnCompleted("session-a"));
        await using var events = adapter.Events.GetAsyncEnumerator();
        await events.MoveNextAsync();

        // The provider starts a fresh conversation mid-session (e.g. an operator /clear) — SessionId changes
        // underneath the same driver instance.
        inner.SessionId = "session-b";
        inner.Emit(_TurnCompleted("session-b"));
        inner.Complete();
        await events.MoveNextAsync();

        Received.InOrder(() =>
        {
            sink.Report("pane-1", SessionConversationId.Known("session-a"));
            sink.Report("pane-1", SessionConversationId.Known("session-b"));
        });
    }

    [Fact]
    public async Task Events_NeverCallsTheSink_WhenNoneIsWired()
    {
        var inner = new FakePluginSessionDriver { SessionId = "session-a" };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);
        await adapter.StartAsync(launchOptions: _PaneOneLaunchOptions);

        inner.Emit(_TurnCompleted("session-a"));
        inner.Complete();

        // Draining must not throw for a driver with no conversation sink configured (a unit test that wires none).
        await _DrainAsync(adapter);
    }

    private static PluginTurnCompleted _TurnCompleted(string sessionId) =>
        new() { SessionId = sessionId, Subtype = "success", Result = null, IsError = false };

    private static async Task _DrainAsync(PluginSessionDriverAdapter adapter)
    {
        await foreach (var _ in adapter.Events)
        {
        }
    }
}
