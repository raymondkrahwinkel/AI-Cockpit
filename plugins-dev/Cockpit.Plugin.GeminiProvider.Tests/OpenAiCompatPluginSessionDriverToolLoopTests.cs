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
