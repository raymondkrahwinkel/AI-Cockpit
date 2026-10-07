using Microsoft.Extensions.AI;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.OpenAiCompat;
using NSubstitute;

namespace Cockpit.Plugin.GeminiProvider.Tests;

// The tool loop the shared OpenAiCompat driver runs over a host-mounted toolset (AC-964). One copy of that
// driver serves this whole provider family, so this covers the Grok, OpenRouter and GitHub Models plugins too.
public class OpenAiCompatPluginSessionDriverToolLoopTests
{
    [Fact]
    public async Task SendUserMessage_WhenTheModelCallsATool_RunsItThroughTheHostAndCarriesOn()
    {
        var toolset = new FakeToolset(["set_status"], reachable: ["set_status"]);
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ToolCall("set_status", ("status", "AC-964")), _Stream("status set."));
        var driver = new OpenAiCompatPluginSessionDriver(chatClient, "gpt-5");
        await _StartWithToolsetAsync(driver, toolset);

        await driver.SendUserMessageAsync("set my status");
        var events = await _CollectAsync(driver, evt => evt is PluginTurnCompleted);

        // The whole point of the ticket: the model's tool call reaches the host, and the turn continues with the
        // result rather than ending on a description of a call that never happened.
        Assert.Equal("set_status", Assert.Single(toolset.Calls).Name);
        Assert.Contains("AC-964", toolset.Calls[0].ArgumentsJson);
        Assert.Contains("status set.", string.Concat(events.OfType<PluginAssistantTextDelta>().Select(delta => delta.Text)));
        Assert.False(Assert.Single(events.OfType<PluginTurnCompleted>()).IsError);
    }

    [Fact]
    public async Task SendUserMessage_AfterADroppedConnection_RetriesWithoutRerunningTheTool_AndLaterTurnsSeeTheToolResult_TrimmedOnceTwoTurnsOld()
    {
        var sent = new List<List<ChatMessage>>();
        var sentResults = new List<string?>();
        var toolset = new FakeToolset(["set_status"], reachable: ["set_status"], result: new string('x', 2000));
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages =>
                {
                    sent.Add([.. messages]);
                    sentResults.Add(messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().SingleOrDefault()?.Result?.ToString());
                }),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(_ToolCall("set_status", ("status", "AC-1431")), _DropConnection("Now I'll "), _Stream("status set. [turn complete]"), _Stream("second turn. [turn complete]"), _Stream("third turn. [turn complete]"));
        var driver = new OpenAiCompatPluginSessionDriver(chatClient, "gpt-5");
        await _StartWithToolsetAsync(driver, toolset);

        await driver.SendUserMessageAsync("set my status");
        var first = await _CollectAsync(driver, evt => evt is PluginTurnCompleted);
        await driver.SendUserMessageAsync("and now?");
        await _CollectAsync(driver, evt => evt is PluginTurnCompleted);
        await driver.SendUserMessageAsync("and then?");
        await _CollectAsync(driver, evt => evt is PluginTurnCompleted);

        // AC-1431 criterion 3: the transport failure is retried into a successful turn, without running the tool twice.
        Assert.False(Assert.Single(first.OfType<PluginTurnCompleted>()).IsError);
        Assert.Single(toolset.Calls);

        // Criterion 1: the second turn carries the first turn's call and its result, the call first.
        var history = sent[3];
        var call = history.FindIndex(message => message.Role == ChatRole.Assistant && message.Contents.OfType<FunctionCallContent>().Any(content => content.CallId == "call_set_status"));
        var result = history.FindIndex(message => message.Role == ChatRole.Tool && message.Contents.OfType<FunctionResultContent>().Any(content => content.CallId == "call_set_status"));
        Assert.InRange(call, 0, result - 1);
        Assert.Equal(["system", "user", "assistant", "tool", "assistant", "user"], history.Select(message => message.Role.Value));

        // The previous turn's result rides along whole; two turns on, only its first 1500 characters and a note do.
        Assert.Equal(2000, sentResults[3]?.Length);
        Assert.Equal(new string('x', 1500) + "\n[truncated by cockpit: 2000 chars]", sentResults[4]);
    }

    [Fact]
    public async Task SendUserMessage_WhenTheModelStopsOnAnAnnouncement_GetsAtMostOneContinuationRound()
    {
        var sent = new List<List<ChatMessage>>();
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Do<IEnumerable<ChatMessage>>(messages => sent.Add([.. messages])), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ToolCall("set_status", ("status", "AC-1431")), _Stream("Now I'll inspect the diff."));
        var driver = new OpenAiCompatPluginSessionDriver(chatClient, "gpt-5");
        await _StartWithToolsetAsync(driver, new FakeToolset(["set_status"], reachable: ["set_status"]));

        await driver.SendUserMessageAsync("work until the PR is open");
        var events = await _CollectAsync(driver, evt => evt is PluginTurnCompleted);

        // AC-1431 criterion 2: an announcement without a tool call or the end-of-turn marker is nudged on within the
        // same turn, and a model that never finishes gets exactly one nudge (AC-1489): the tool round, its answer, one more.
        Assert.Equal(3, sent.Count);
        Assert.Single(sent[^1], message => message.Role == ChatRole.User && message.Text == ChatTurnLoop.ContinuationNudge);
        Assert.Contains(ChatTurnLoop.CompletionMarker, sent[0][0].Text);
        Assert.False(Assert.Single(events.OfType<PluginTurnCompleted>()).IsError);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> _DropConnection(string partial)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, partial);
        await Task.CompletedTask;
        throw new IOException("Unable to read data from the transport connection.");
    }

    private static Task _StartWithToolsetAsync(OpenAiCompatPluginSessionDriver driver, IPluginToolset toolset) =>
        driver.StartAsync(null, null, null, null, null, null, toolset, CancellationToken.None);

    // A toolset that records what it was asked to run. Its descriptors carry a real schema, because the schema
    // is what the model client builds its function definitions from — a broken one would fail at the wire, not here.
    private sealed class FakeToolset(IReadOnlyList<string> tools, IReadOnlyList<string> reachable, string result = "done") : IPluginToolset
    {
        public List<(string Name, string ArgumentsJson)> Calls { get; } = [];

        public IReadOnlyList<PluginToolDescriptor> Tools { get; } =
            [.. tools.Select(name => new PluginToolDescriptor("cockpit-session", name, $"Does {name}.", """{"type":"object","properties":{"status":{"type":"string"}}}"""))];

        public IReadOnlyList<string> ReachableToolNames { get; } = reachable;

        public Task<string> InvokeAsync(string name, string argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((name, argumentsJson));
            return Task.FromResult(result);
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> _Stream(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> _ToolCall(string name, params (string Key, object? Value)[] args)
    {
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent($"call_{name}", name, args.ToDictionary(pair => pair.Key, pair => pair.Value))],
        };

        await Task.CompletedTask;
    }

    private static async Task<List<PluginSessionEvent>> _CollectAsync(IPluginSessionDriver driver, Func<PluginSessionEvent, bool> until)
    {
        var collected = new List<PluginSessionEvent>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var sessionEvent in driver.Events.WithCancellation(deadline.Token))
        {
            collected.Add(sessionEvent);
            if (until(sessionEvent))
            {
                break;
            }
        }

        return collected;
    }
}
