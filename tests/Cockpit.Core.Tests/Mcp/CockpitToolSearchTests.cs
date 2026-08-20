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
    public void SearchTools_MatchesNameAndDescription_AndReportsServerAndSchema()
    {
        var catalog = new[]
        {
            _Tool("set_status", "Sets your session's statusline.", "cockpit-session"),
            _Tool("read_file", "Reads a file from disk.", "filesystem"),
        };

        var hits = _Search(catalog, "statusline");

        var match = Assert.Single(hits.GetProperty("matches").EnumerateArray());
        Assert.Equal("set_status", match.GetProperty("name").GetString());
        Assert.Equal("cockpit-session", match.GetProperty("server").GetString());
        Assert.Contains("statusline", match.GetProperty("description").GetString());
        // The schema is what makes a hit callable without it ever being in the prompt — a match without one would
        // leave the model guessing parameter names.
        Assert.Equal(JsonValueKind.Object, match.GetProperty("input_schema").ValueKind);
    }

    [Fact]
    public void SearchTools_SaysWhenItTruncated_AndSaysWhenNothingMatched()
    {
        var catalog = Enumerable.Range(0, 12).Select(index => _Tool($"note_{index}", "Writes a note.", "notes")).ToArray();

        // Criterion 3: "more matched than you can see" and "nothing matched" must not read the same to a model —
        // silently cutting the list would let it conclude a tool does not exist when it was simply hit 11 of 12.
        var truncated = _Search(catalog, "note", limit: 3);
        Assert.Equal(3, truncated.GetProperty("matches").GetArrayLength());
        Assert.Equal(12, truncated.GetProperty("total_matches").GetInt32());
        Assert.Contains("showing 3 of 12", truncated.GetProperty("note").GetString(), StringComparison.OrdinalIgnoreCase);

        var nothing = _Search(catalog, "youtrack");
        Assert.Equal(0, nothing.GetProperty("matches").GetArrayLength());
        Assert.Equal(0, nothing.GetProperty("total_matches").GetInt32());
        Assert.Contains("No tool matched", nothing.GetProperty("note").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SearchTools_WithAServerFilter_KeepsToThatServer_AndNamesTheOnesItKnows()
    {
        var catalog = new[]
        {
            _Tool("set_status", "Sets your session's statusline.", "cockpit-session"),
            _Tool("read_file", "Reads a file.", "filesystem"),
        };

        Assert.Equal("read_file", Assert.Single(_Search(catalog, string.Empty, server: "filesystem")
            .GetProperty("matches").EnumerateArray()).GetProperty("name").GetString());

        var unknown = _Search(catalog, "file", server: "nope");
        Assert.Contains("cockpit-session, filesystem", unknown.GetProperty("note").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AboveTheThreshold_OnlyTheAlwaysMountedToolsAndTheTwoProxiesRideAlong()
    {
        var catalog = _CatalogOf(CockpitToolSearch.PreloadThreshold + 1, alwaysMounted: "set_status");
        var (driver, options) = await _StartAsync(catalog, _Stream("hi"));
        await driver.SendUserMessageAsync("hello");
        await _CollectUntilTurnCompletedAsync(driver);

        // Criterion 5: the whole point — a catalogue this size is 25-40k tokens of schema per request. What is left
        // is the plumbing that must never go behind a search (set_status) plus the two proxies.
        Assert.Equal(
            new[] { CockpitToolSearch.CallToolName, CockpitToolSearch.SearchToolName, "set_status" },
            options.Single()!.Tools!.Select(tool => tool.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AtTheThreshold_TheWholeCatalogueStillRidesAlong()
    {
        var catalog = _CatalogOf(CockpitToolSearch.PreloadThreshold, alwaysMounted: "set_status");
        var (driver, options) = await _StartAsync(catalog, _Stream("hi"));
        await driver.SendUserMessageAsync("hello");
        await _CollectUntilTurnCompletedAsync(driver);

        // Criterion 5's other half: a session under the threshold behaves exactly as it did before this existed —
        // every tool preloaded, and no search_tools/call_tool in sight.
        var sent = options.Single()!.Tools!.Select(tool => tool.Name).ToList();
        Assert.Equal(CockpitToolSearch.PreloadThreshold, sent.Count);
        Assert.DoesNotContain(CockpitToolSearch.SearchToolName, sent);
    }

    [Fact]
    public async Task SessionInitialized_NamesTheWholeCatalogue_PlusTheProxiesThatReachIt()
    {
        var catalog = _CatalogOf(CockpitToolSearch.PreloadThreshold + 1, alwaysMounted: "set_status");
        var (driver, _) = await _StartAsync(catalog, _Stream("hi"));
        var init = Assert.IsType<SessionInitialized>(Assert.Single(await _DrainAsync(driver, 1)));

        // Criterion 6: the count has to stay a statement about what the model can reach. In search mode that is
        // still every tool — through call_tool — so reporting only the three preloaded ones would understate it.
        Assert.Equal(CockpitToolSearch.PreloadThreshold + 3, init.Tools.Count);
        Assert.Contains(CockpitToolSearch.SearchToolName, init.Tools);
        Assert.Contains(CockpitToolSearch.CallToolName, init.Tools);
    }

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

    [Fact]
    public async Task CallTool_WithAnUnknownName_SaysSo_WithoutRunningAnything()
    {
        var catalog = _CatalogOf(CockpitToolSearch.PreloadThreshold + 1, alwaysMounted: "set_status");
        var (driver, _) = await _StartAsync(catalog, _ProxyCall("nope", "{}"), _Stream("done"));
        await driver.SendUserMessageAsync("go");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        Assert.Empty(events.OfType<ToolUseRequested>());
        Assert.Empty(events.OfType<PermissionRequested>());
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

    private static JsonElement _Search(IReadOnlyList<McpSessionTool> catalog, string query, string? server = null, int? limit = null)
    {
        var search = (AIFunction)CockpitToolSearch.Build(catalog).Single(tool => tool.Name == CockpitToolSearch.SearchToolName);
        var arguments = new AIFunctionArguments { ["query"] = query, ["server"] = server, ["limit"] = limit };
        return JsonDocument.Parse(search.InvokeAsync(arguments).AsTask().GetAwaiter().GetResult()!.ToString()!).RootElement;
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

    private static async Task<List<SessionEvent>> _DrainAsync(ISessionDriver driver, int count)
    {
        var events = new List<SessionEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var evt in driver.Events.WithCancellation(cts.Token))
        {
            events.Add(evt);
            if (events.Count == count)
            {
                break;
            }
        }

        return events;
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
