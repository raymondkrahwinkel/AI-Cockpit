using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// AC-963: the <c>cockpit-tools</c> search layer — <c>search_tools</c> and <c>call_tool</c> — and the threshold that
/// decides whether <see cref="OpenAiCompatSessionDriver"/> preloads its whole catalogue or keeps it out of the
/// prompt. The load-bearing test here is the gate one: <c>call_tool</c> must run the session's already-gated
/// function, never the tool underneath it, or it is the back door a delegated session escapes AC-79 through.
/// </summary>
public class CockpitToolSearchTests
{
    private static readonly SessionProfile LocalProfile =
        new("local", new OllamaConfig("http://localhost:11434", "llama3.1"));

    [Fact]
    public async Task CallTool_CannotRunWhatTheDelegationCeilingRefuses_AndRefusesItInTheSameWords()
    {
        // The security criterion (4): the AC-79 ceiling lives in the GatedTool around the real tool, so a call_tool
        // that reached past it would be a permission bypass with a friendly name. Proven red by pointing it at the
        // raw AIFunction — `ran` flips true and no ToolResult error.
        var ran = false;
        var echo = AIFunctionFactory.Create((string text) => { ran = true; return $"echoed:{text}"; }, "echo");
        var catalog = _CatalogOf(CockpitToolSearch.PreloadThreshold + 1, alwaysMounted: "set_status", extra: echo);

        var (driver, _) = await _StartAsync(
            catalog,
            _ProxyCall("echo", "{\"text\":\"hi\"}"),
            _Stream("done"),
            toolClasses: new Dictionary<string, ToolPermissionClass> { ["echo"] = ToolPermissionClass.Unknown });
        await driver.SetDelegatedToolGateAsync("acceptEdits", []);
        await driver.SendUserMessageAsync("go");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        Assert.False(ran);
        Assert.Empty(events.OfType<PermissionRequested>());
        var result = Assert.Single(events.OfType<ToolResult>());
        Assert.True(result.IsError);
        Assert.DoesNotContain("echoed:hi", result.Content);

        // The gate is reached under the real tool's own name, so the refusal is word for word the one a direct call
        // gets — the model must not be able to tell the two routes apart and go looking for a way around.
        Assert.Equal(await _DirectDenialTextAsync(), result.Content);
    }

    [Fact]
    public async Task CallTool_RunsTheToolThroughTheGate_WhenTheCeilingAllowsIt()
    {
        var ran = false;
        var echo = AIFunctionFactory.Create((string text) => { ran = true; return $"echoed:{text}"; }, "echo");
        var catalog = _CatalogOf(CockpitToolSearch.PreloadThreshold + 1, alwaysMounted: "set_status", extra: echo);

        var (driver, _) = await _StartAsync(
            catalog,
            _ProxyCall("echo", "{\"text\":\"hi\"}"),
            _Stream("done"),
            toolClasses: new Dictionary<string, ToolPermissionClass> { ["echo"] = ToolPermissionClass.Unknown });
        await driver.SetDelegatedToolGateAsync("plan", ["echo"]);
        await driver.SendUserMessageAsync("go");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        Assert.True(ran);
        // The transcript names the tool that actually ran, not the proxy that carried it — an operator reading the
        // session must see "echo", not "call_tool".
        Assert.Equal("echo", Assert.Single(events.OfType<ToolUseRequested>()).ToolName);
        Assert.Contains("echoed:hi", Assert.Single(events.OfType<ToolResult>()).Content);
    }

    // The refusal a *direct* call to the same tool under the same ceiling produces, for the comparison above.
    private static async Task<string> _DirectDenialTextAsync()
    {
        var echo = AIFunctionFactory.Create((string text) => $"echoed:{text}", "echo");
        var (driver, _) = await _StartAsync(
            [new McpSessionTool(echo, "test-server", AlwaysMounted: false)],
            _ToolCall("echo", ("text", "hi")),
            _Stream("done"),
            toolClasses: new Dictionary<string, ToolPermissionClass> { ["echo"] = ToolPermissionClass.Unknown });
        await driver.SetDelegatedToolGateAsync("acceptEdits", []);
        await driver.SendUserMessageAsync("go");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        return Assert.Single(events.OfType<ToolResult>()).Content;
    }

    private static McpSessionTool _Tool(string name, string description, string server, bool alwaysMounted = false) =>
        new(AIFunctionFactory.Create((string text) => text, name, description), server, alwaysMounted);

    // A catalogue of `count` filler tools, one of them named `alwaysMounted` and flagged as such, plus any extras.
    private static McpSessionTool[] _CatalogOf(int count, string alwaysMounted, params AIFunction[] extra) =>
    [
        _Tool(alwaysMounted, "Sets your session's statusline.", "cockpit-session", alwaysMounted: true),
        .. Enumerable.Range(0, count - 1).Select(index => _Tool($"filler_{index}", "A mounted tool.", "test-server")),
        .. extra.Select(function => new McpSessionTool(function, "test-server", AlwaysMounted: false)),
    ];

    private static async Task<(OpenAiCompatSessionDriver Driver, List<ChatOptions?> Options)> _StartAsync(
        IReadOnlyList<McpSessionTool> catalog,
        IAsyncEnumerable<ChatResponseUpdate> first,
        IAsyncEnumerable<ChatResponseUpdate>? second = null,
        IReadOnlyDictionary<string, ToolPermissionClass>? toolClasses = null)
    {
        var options = new List<ChatOptions?>();
        var chatClient = Substitute.For<IChatClient>();
        var call = chatClient.GetStreamingResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Do<ChatOptions?>(sent => options.Add(sent)),
            Arg.Any<CancellationToken>());
        if (second is null)
        {
            call.Returns(first);
        }
        else
        {
            call.Returns(first, second);
        }

        var toolSession = Substitute.For<IMcpToolSession>();
        toolSession.Tools.Returns(catalog);
        toolSession.ConnectedServerNames.Returns(["test-server"]);
        toolSession.ToolClasses.Returns(toolClasses ?? new Dictionary<string, ToolPermissionClass>());
        var toolProvider = Substitute.For<IMcpToolProvider>();
        toolProvider.ConnectAsync(Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(toolSession);
        var factory = Substitute.For<IChatClientFactory>();
        factory.Create(Arg.Any<ProviderConfig>()).Returns(chatClient);

        var driver = new OpenAiCompatSessionDriver(factory, toolProvider, NullLogger<OpenAiCompatSessionDriver>.Instance);
        await driver.StartAsync(LocalProfile);
        return (driver, options);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> _Stream(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }

        await Task.CompletedTask;
    }

    private static IAsyncEnumerable<ChatResponseUpdate> _ToolCall(string name, params (string Key, object? Value)[] args) =>
        _Call(name, args.ToDictionary(pair => pair.Key, pair => pair.Value));

    // The model reaching a tool the way search mode makes it: through the proxy, with the real arguments as JSON.
    private static IAsyncEnumerable<ChatResponseUpdate> _ProxyCall(string name, string argumentsJson) =>
        _Call(CockpitToolSearch.CallToolName, new Dictionary<string, object?>
        {
            ["server"] = "test-server",
            ["name"] = name,
            ["arguments"] = JsonDocument.Parse(argumentsJson).RootElement.Clone(),
        });

    private static async IAsyncEnumerable<ChatResponseUpdate> _Call(string name, IDictionary<string, object?> arguments)
    {
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent($"call_{name}", name, arguments)],
        };

        await Task.CompletedTask;
    }

    private static async Task<List<SessionEvent>> _CollectUntilTurnCompletedAsync(ISessionDriver driver)
    {
        var events = new List<SessionEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var evt in driver.Events.WithCancellation(cts.Token))
        {
            events.Add(evt);
            if (evt is TurnCompleted)
            {
                break;
            }
        }

        return events;
    }
}
