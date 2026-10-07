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

    [Fact]
    public async Task SendUserMessage_WithLargeToolResults_CapsEachOne_AndPrunesOlderOnesBelowTheContextLimit_KeepingEveryCallWithItsResult()
    {
        var limits = new ChatTurnLimits { ContextWindowTokens = 50_000, SoftLimitRatio = 0.5, ReserveTokens = 20_000, ProtectRecentTokens = 15_000 };
        var bigResult = string.Concat(Enumerable.Repeat(new string('x', 99) + "\n", 1024));
        var requests = new List<(List<ChatMessage> Messages, List<string> Results, long Chars)>();
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Do<IEnumerable<ChatMessage>>(messages => requests.Add(_Snapshot(messages))), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ToolCall("read_a"), _ToolCall("read_b"), _ToolCall("read_c"), _Stream("read all three. [turn complete]"));
        var driver = new OpenAiCompatPluginSessionDriver(chatClient, "gpt-5", turnLimits: limits);
        await _StartWithToolsetAsync(driver, new FakeToolset(["read_a", "read_b", "read_c"], reachable: ["read_a", "read_b", "read_c"], result: bigResult));

        await driver.SendUserMessageAsync("read the three files");
        await _CollectAsync(driver, evt => evt is PluginTurnCompleted);

        // AC-1489 criterion 1: the very next request carries the 100 KiB result cut to 50 KiB, with the cut line.
        var firstResult = Assert.Single(requests[1].Results);
        Assert.StartsWith(bigResult[..51_200], firstResult);
        Assert.Contains($"[cockpit: output cut at 51200 of {bigResult.Length} chars", firstResult);
        Assert.InRange(firstResult.Length, 51_200, 51_400);

        // Criterion 2: past the soft limit older results are pruned and the newest stays whole, so no request reaches
        // the hard limit; the system prompt and the task are untouched, and every call still has its result.
        var last = requests[^1];
        Assert.All(last.Results.Take(2), result => Assert.Contains("[cleared by cockpit to stay within the context", result));
        Assert.StartsWith(bigResult[..51_200], last.Results[2]);
        Assert.All(requests, request => Assert.InRange(request.Chars / 4, 0, limits.ContextWindowTokens - limits.ReserveTokens));
        Assert.Equal(["system", "user"], last.Messages.Take(2).Select(message => message.Role.Value));
        Assert.Equal("read the three files", last.Messages[1].Text);
        Assert.All(requests, request => Assert.Equal(
            request.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Select(call => call.CallId),
            request.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(result => result.CallId)));
    }

    [Fact]
    public async Task SendUserMessage_WhenTheModelKeepsCallingTools_DropsTheToolsOnceTheBudgetIsUsedUp_AndEndsTheTurnWithoutANudge()
    {
        var requests = new List<(List<ChatMessage> Messages, ChatOptions? Options)>();
        var callCount = 0;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var options = call.ArgAt<ChatOptions?>(1);
                requests.Add(([.. call.ArgAt<IEnumerable<ChatMessage>>(0)], options));
                return options?.Tools is null ? _Stream("Read three files; the rest is left.") : _ToolCall("read", $"call_{++callCount}");
            });
        var toolset = new FakeToolset(["read"], reachable: ["read"], result: new string('x', 1000));
        var driver = new OpenAiCompatPluginSessionDriver(chatClient, "gpt-5", turnLimits: new ChatTurnLimits { TurnToolBudgetChars = 2_500 });
        await _StartWithToolsetAsync(driver, toolset);

        await driver.SendUserMessageAsync("read every file in the repo");
        var events = await _CollectAsync(driver, evt => evt is PluginTurnCompleted);

        // AC-1489 criterion 3: three results pass the 2500-char budget, so the fourth request goes without tools and
        // with the wrap-up note; its text ends the turn even without the marker, because a wound-down turn gets no nudge.
        Assert.Equal(3, toolset.Calls.Count);
        Assert.Equal(4, requests.Count);
        Assert.Null(requests[^1].Options?.Tools);
        Assert.Equal(ContextGuardChatClient.BudgetNote, requests[^1].Messages[^1].Text);
        Assert.DoesNotContain(requests.SelectMany(request => request.Messages), message => message.Text == ChatTurnLoop.ContinuationNudge);
        Assert.False(Assert.Single(events.OfType<PluginTurnCompleted>()).IsError);
    }

    // What a request held at the moment it went out: the guard shortens results in place afterwards.
    private static (List<ChatMessage> Messages, List<string> Results, long Chars) _Snapshot(IEnumerable<ChatMessage> messages)
    {
        List<ChatMessage> list = [.. messages];
        List<string> results = [.. list.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(result => result.Result?.ToString() ?? string.Empty)];
        return (list, results, list.Sum(message => (long)message.Text.Length) + results.Sum(result => (long)result.Length));
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

    private static IAsyncEnumerable<ChatResponseUpdate> _ToolCall(string name, params (string Key, object? Value)[] args) => _ToolCall(name, $"call_{name}", args);

    private static async IAsyncEnumerable<ChatResponseUpdate> _ToolCall(string name, string callId, params (string Key, object? Value)[] args)
    {
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent(callId, name, args.ToDictionary(pair => pair.Key, pair => pair.Value))],
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
