using Microsoft.Extensions.AI;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// <see cref="OpenAiCompatSessionDriver"/> against a fake <see cref="IChatClient"/>: a streamed reply
/// surfaces as ordered <see cref="AssistantTextDelta"/> events followed by a successful
/// <see cref="TurnCompleted"/>, and the driver advertises chat-only capabilities (no tools yet).
/// </summary>
public class OpenAiCompatSessionDriverTests
{
    private static readonly SessionProfile LocalProfile =
        new("local", new OllamaConfig("http://localhost:11434", "llama3.1"));

    [Fact]
    public async Task LmStudio_OffersCodingNatively_WithTheWorkingDirectoryInTheSystemPrompt()
    {
        Assert.Equal(Timeout.InfiniteTimeSpan, OpenAiCompatChatClientFactory.BuildClientOptions("http://localhost:1234").NetworkTimeout);
        List<ChatMessage>? sent = null;
        ChatOptions? sentOptions = null;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages => sent = [.. messages]),
                Arg.Do<ChatOptions>(options => sentOptions = options),
                Arg.Any<CancellationToken>())
            .Returns(_Stream("Hello ", "world."));
        var contextManager = Substitute.For<ILocalModelContextManager>();
        contextManager.PrepareAsync(Arg.Any<ProviderConfig>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("LM Studio is geladen met 8192 tokens; het profiel vraagt 32768 tokens.");
        var driver = _CreateDriver(chatClient, _SelectionSensitiveProvider(), contextManager);
        var profile = new SessionProfile("lmstudio", new LmStudioConfig("http://localhost:1234", "qwen3-coder", EnableThinking: true))
        {
            EnabledMcpServerNames = ["test-server"],
        };

        await driver.StartAsync(profile, workingDirectory: @"C:\\work\\AC-1911");
        await driver.SendUserMessageAsync("hi");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        Assert.Equal("Hello world.", string.Concat(events.OfType<AssistantTextDelta>().Select(delta => delta.Text)));
        Assert.False(Assert.Single(events.OfType<TurnCompleted>()).IsError);
        Assert.Single(events, evt => evt is SessionInitialized);
        Assert.Contains(sentOptions!.Tools!, tool => tool.Name == "read_file");
        Assert.Contains(sentOptions.Tools!, tool => tool.Name == "shell");
        Assert.Contains(sentOptions.Tools!, tool => tool.Name == CockpitToolSearch.SearchToolName);
        Assert.DoesNotContain(sentOptions.Tools!, tool => tool.Name == "background_0");
        Assert.Null(sentOptions.RawRepresentationFactory);
        var systemPrompt = Assert.Single(sent!, message => message.Role == ChatRole.System).Text;
        Assert.Contains(@"C:\\work\\AC-1911", systemPrompt);
        Assert.Contains(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux", systemPrompt);
        Assert.Contains("relative paths resolve against this working directory", systemPrompt);
        Assert.Contains("verify the requested result", systemPrompt);
        Assert.Contains(events.OfType<AssistantTextCompleted>(), completed => completed.Text.Contains("geladen met 8192", StringComparison.Ordinal));
        await LocalModelContextContract.AssertAsync();
        var exact = new Cockpit.Core.Mcp.ExactMcpServerSelection(["Depot", "YouTrack", "cockpit-coding-shell"]);
        var provider = _SelectionSensitiveProvider();
        var restricted = _CreateDriver(chatClient, provider, contextManager);
        // AC-1962: an isolated start forwards the complete selection and still vouches for its own file tools.
        await restricted.StartAsync(profile, enabledMcpServerNames: exact, workingDirectory: "/wt/run-1",
            launchOptions: new Dictionary<string, string> { [Cockpit.Plugins.Abstractions.Sessions.WellKnownPluginSessionOptions.ConfineFileToolsToWorkingDirectory] = "true" });
        Assert.True(restricted.Capabilities.ConfinesFileAccessToWorkingDirectory);
        await provider.Received(1).ConnectAsync(
            Arg.Is<IReadOnlySet<string>?>(selection => ReferenceEquals(selection, exact)),
            Arg.Any<string?>(), "/wt/run-1", Arg.Any<string?>(), "/wt/run-1",
            Arg.Any<CancellationToken>(), Arg.Any<string?>());
        await restricted.SendUserMessageAsync("hi");
        await _CollectUntilTurnCompletedAsync(restricted);
        foreach (var thinking in new bool?[] { false, null })
        {
            var alternate = _CreateDriver(chatClient, _SelectionSensitiveProvider(), contextManager);
            await alternate.StartAsync(new SessionProfile("alternate", Assert.IsType<LmStudioConfig>(profile.ProviderConfig) with { EnableThinking = thinking }));
            await alternate.SendUserMessageAsync("hi");
            await _CollectUntilTurnCompletedAsync(alternate);
            if (thinking is null)
            {
                Assert.Null(sentOptions!.RawRepresentationFactory);
            }
            else
            {
                var requestOptions = Assert.IsType<OpenAI.Chat.ChatCompletionOptions>(sentOptions!.RawRepresentationFactory!(chatClient));
                using var request = System.Text.Json.JsonDocument.Parse(System.ClientModel.Primitives.ModelReaderWriter.Write(requestOptions).ToString());
                Assert.Equal("none", request.RootElement.GetProperty("reasoning_effort").GetString());
            }
        }

        var overrideConfig = Assert.IsType<LmStudioConfig>(profile.ProviderConfig) with
        {
            ContextTokens = 65_536,
            LoadModelWithContext = true,
            TurnLimits = System.Text.Json.JsonSerializer.SerializeToElement(new { contextWindowTokens = 32_768 }),
        };
        var overridden = _CreateDriver(chatClient, _SelectionSensitiveProvider(), contextManager);
        await overridden.StartAsync(profile with { Label = "override", ProviderConfig = overrideConfig }, model: "other-model");
        await contextManager.Received(1).PrepareAsync(
            Arg.Is<LmStudioConfig>(config => config.Model == "other-model" && config.ContextTokens == 65_536 && config.LoadModelWithContext && config.LoadOptions == overrideConfig.LoadOptions),
            Arg.Any<CancellationToken>(), Arg.Any<string?>());
        await overridden.SendUserMessageAsync(new string('x', 40_000 * 4));
        var largeTurn = await _CollectUntilTurnCompletedAsync(overridden);
        Assert.False(Assert.Single(largeTurn.OfType<TurnCompleted>()).IsError);
        Assert.NotEmpty(sentOptions!.Tools!);
        Assert.Equal("other-model", sentOptions.ModelId);
        Assert.DoesNotContain(sent!, message => message.Text == Cockpit.Plugins.OpenAiCompat.ContextGuardChatClient.ContextFullNote);
        await overridden.DisposeAsync();
        await contextManager.Received(1).ReleaseAsync(overridden.SessionId!);
        var failedManager = Substitute.For<ILocalModelContextManager>();
        failedManager.PrepareAsync(Arg.Any<ProviderConfig>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(Task.FromException<string?>(new InvalidOperationException("context could not be applied")));
        var failedFactory = Substitute.For<IChatClientFactory>();
        var failed = new OpenAiCompatSessionDriver(failedFactory, _SelectionSensitiveProvider(), NullLogger<OpenAiCompatSessionDriver>.Instance, failedManager);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed.StartAsync(profile));
        failedFactory.DidNotReceive().Create(Arg.Any<ProviderConfig>());
        await failedManager.Received(1).ReleaseAsync(failed.SessionId!);
        var faultedToolSession = Substitute.For<IMcpToolSession>();
        faultedToolSession.Tools.Returns([]);
        faultedToolSession.ConnectedServerNames.Returns([]);
        var faultedProvider = Substitute.For<IMcpToolProvider>();
        faultedProvider.ConnectAsync(Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(faultedToolSession);
        var faulted = _CreateDriver(chatClient, faultedProvider, contextManager);
        await faulted.StartAsync(profile);
        typeof(OpenAiCompatSessionDriver).GetField("_turnTask", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(faulted, Task.FromException(new InvalidOperationException("turn cleanup failed")));
        var cleanupError = await Assert.ThrowsAsync<InvalidOperationException>(() => faulted.DisposeAsync().AsTask());
        Assert.Equal("turn cleanup failed", cleanupError.Message);
        await faultedToolSession.Received(1).DisposeAsync();
        await contextManager.Received(1).ReleaseAsync(faulted.SessionId!);
    }

    [Fact]
    public async Task ToolApproval_EmitsToolUseAndPermissionRequested_AndRespondCompletesTheDecision()
    {
        // Driven through a real tool call rather than the gate interface: the gate moved to its own type
        // (AC-964, shared with the plugin-provider loop), so the seam worth testing is what a turn produces.
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ToolCall("read_file", ("path", "x")), _Stream("done"));
        var driver = _CreateDriver(chatClient,
            [AIFunctionFactory.Create((string path) => $"read {path}", "read_file"),
                .. Enumerable.Range(0, CockpitToolSearch.PreloadThreshold).Select(index => AIFunctionFactory.Create(() => "unused", $"filler_{index}"))]);
        await driver.StartAsync(LocalProfile);

        await driver.SendUserMessageAsync("go");
        var events = await _CollectUntilAsync(driver, evt => evt is PermissionRequested);

        var prompt = Assert.Single(events.OfType<PermissionRequested>());
        Assert.Equal("read_file", prompt.ToolName);
        Assert.Contains(events, evt => evt is ToolUseRequested);

        await driver.RespondToPermissionAsync(prompt.ToolUseId, allow: true);
        var completed = await _CollectUntilTurnCompletedAsync(driver);
        Assert.Contains("read x", completed.OfType<ToolResult>().Single().Content);
    }

    [Fact]
    public async Task DelegatedGate_RunsAToolWithinTheCeiling_WithoutAPrompt()
    {
        // AC-79: a delegated session decides tool calls against the ceiling — a read-only tool runs under any
        // ceiling, non-interactively, with no PermissionRequested.
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ToolCall("echo", ("text", "hi")), _Stream("done"));
        var echo = AIFunctionFactory.Create((string text) => $"echoed:{text}", "echo");
        var driver = _CreateDriver(chatClient, new Dictionary<string, ToolPermissionClass> { ["echo"] = ToolPermissionClass.ReadOnly }, echo);

        await driver.StartAsync(LocalProfile);
        await driver.SetDelegatedToolGateAsync("plan", []);
        await driver.SendUserMessageAsync("go");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        Assert.Empty(events.OfType<PermissionRequested>());
        Assert.False(Assert.Single(events.OfType<ToolResult>()).IsError);
        Assert.Contains("echoed:hi", events.OfType<ToolResult>().Single().Content);
    }

    [Fact]
    public async Task DelegatedGate_DeniesAToolAboveTheCeiling_WithReasonAndNoPrompt()
    {
        // A destructive tool is denied under acceptEdits: no prompt (nobody to answer), and the denial is fed back
        // as the tool result so the model can adapt rather than hang.
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ToolCall("echo", ("text", "hi")), _Stream("done"));
        var echo = AIFunctionFactory.Create((string text) => $"echoed:{text}", "echo");
        var driver = _CreateDriver(chatClient, new Dictionary<string, ToolPermissionClass> { ["echo"] = ToolPermissionClass.Destructive }, echo);

        await driver.StartAsync(LocalProfile);
        await driver.SetDelegatedToolGateAsync("acceptEdits", []);
        await driver.SendUserMessageAsync("go");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        Assert.Empty(events.OfType<PermissionRequested>());
        var result = Assert.Single(events.OfType<ToolResult>());
        Assert.True(result.IsError);
        Assert.DoesNotContain("echoed:hi", result.Content);
    }

    [Fact]
    public async Task DelegatedGate_DeniesAnUnknownTool_UnlessOnTheAllowList()
    {
        // An unclassifiable tool is denied even at bypassPermissions when not allow-listed...
        var deniedClient = Substitute.For<IChatClient>();
        deniedClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ToolCall("echo", ("text", "hi")), _Stream("done"));
        var denied = _CreateDriver(deniedClient, new Dictionary<string, ToolPermissionClass> { ["echo"] = ToolPermissionClass.Unknown }, AIFunctionFactory.Create((string text) => $"echoed:{text}", "echo"));
        await denied.StartAsync(LocalProfile);
        await denied.SetDelegatedToolGateAsync("bypassPermissions", []);
        await denied.SendUserMessageAsync("go");
        var deniedEvents = await _CollectUntilTurnCompletedAsync(denied);

        Assert.Empty(deniedEvents.OfType<PermissionRequested>());
        Assert.True(Assert.Single(deniedEvents.OfType<ToolResult>()).IsError);

        // ...but runs when the operator listed it, even under the most restrictive ceiling.
        var allowedClient = Substitute.For<IChatClient>();
        allowedClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ToolCall("echo", ("text", "hi")), _Stream("done"));
        var allowed = _CreateDriver(allowedClient, new Dictionary<string, ToolPermissionClass> { ["echo"] = ToolPermissionClass.Unknown }, AIFunctionFactory.Create((string text) => $"echoed:{text}", "echo"));
        await allowed.StartAsync(LocalProfile);
        await allowed.SetDelegatedToolGateAsync("plan", ["echo"]);
        await allowed.SendUserMessageAsync("go");
        var allowedEvents = await _CollectUntilTurnCompletedAsync(allowed);

        Assert.Empty(allowedEvents.OfType<PermissionRequested>());
        Assert.Contains("echoed:hi", Assert.Single(allowedEvents.OfType<ToolResult>()).Content);
    }

    [Fact]
    public async Task DelegatedGate_ToolMissingFromTheClassMap_IsTreatedAsUnknownAndDenied()
    {
        // Fail-safe: a tool the classification map has no entry for defaults to Unknown → denied, even at the most
        // permissive ceiling, with no prompt. Guards against a regression that dropped the explicit Unknown fallback.
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ToolCall("echo", ("text", "hi")), _Stream("done"));
        var echo = AIFunctionFactory.Create((string text) => $"echoed:{text}", "echo");
        // Empty class map — "echo" is absent.
        var driver = _CreateDriver(chatClient, new Dictionary<string, ToolPermissionClass>(), echo);

        await driver.StartAsync(LocalProfile);
        await driver.SetDelegatedToolGateAsync("bypassPermissions", []);
        await driver.SendUserMessageAsync("go");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        Assert.Empty(events.OfType<PermissionRequested>());
        var result = Assert.Single(events.OfType<ToolResult>());
        Assert.True(result.IsError);
        Assert.DoesNotContain("echoed:hi", result.Content);
    }

    public static IEnumerable<object[]> RepliesWithNothingToShow() =>
    [
        [Array.Empty<string>()],
        [new[] { "   ", "\n" }],
    ];

    private static OpenAiCompatSessionDriver _CreateDriver(IChatClient chatClient, params AIFunction[] tools) =>
        _CreateDriver(chatClient, new Dictionary<string, ToolPermissionClass>(), tools);

    private static OpenAiCompatSessionDriver _CreateDriver(IChatClient chatClient, IMcpToolProvider toolProvider, ILocalModelContextManager? contextManager)
    {
        var factory = Substitute.For<IChatClientFactory>();
        factory.Create(Arg.Any<ProviderConfig>()).Returns(chatClient);
        return new OpenAiCompatSessionDriver(factory, toolProvider, NullLogger<OpenAiCompatSessionDriver>.Instance, contextManager ?? Substitute.For<ILocalModelContextManager>());
    }

    private static OpenAiCompatSessionDriver _CreateDriver(IChatClient chatClient, IReadOnlyDictionary<string, ToolPermissionClass> toolClasses, params AIFunction[] tools)
    {
        var factory = Substitute.For<IChatClientFactory>();
        factory.Create(Arg.Any<ProviderConfig>()).Returns(chatClient);

        var toolSession = Substitute.For<IMcpToolSession>();
        toolSession.Tools.Returns([.. tools.Select(tool => new McpSessionTool(tool, "test-server", AlwaysMounted: false))]);
        toolSession.ConnectedServerNames.Returns(tools.Length == 0 ? Array.Empty<string>() : new[] { "test-server" });
        toolSession.ToolClasses.Returns(toolClasses);
        var toolProvider = Substitute.For<IMcpToolProvider>();
        toolProvider.ConnectAsync(Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>()).Returns(toolSession);

        return new OpenAiCompatSessionDriver(factory, toolProvider, NullLogger<OpenAiCompatSessionDriver>.Instance, Substitute.For<ILocalModelContextManager>());
    }

    private static IMcpToolProvider _SelectionSensitiveProvider()
    {
        var coding = Substitute.For<IMcpToolSession>();
        coding.Tools.Returns(
        [
            new McpSessionTool(AIFunctionFactory.Create((string path) => path, "read_file"), "cockpit-coding", AlwaysMounted: false),
            new McpSessionTool(AIFunctionFactory.Create((string command) => command, "shell"), "cockpit-coding-shell", AlwaysMounted: false),
            .. Enumerable.Range(0, CockpitToolSearch.PreloadThreshold)
                .Select(index => new McpSessionTool(AIFunctionFactory.Create((string value) => value, $"background_{index}"), "test-server", AlwaysMounted: false)),
        ]);
        coding.ConnectedServerNames.Returns(["cockpit-coding", "cockpit-coding-shell", "test-server"]);
        coding.ToolClasses.Returns(new Dictionary<string, ToolPermissionClass>());

        var empty = Substitute.For<IMcpToolSession>();
        empty.Tools.Returns([]);
        empty.ConnectedServerNames.Returns([]);
        empty.ToolClasses.Returns(new Dictionary<string, ToolPermissionClass>());

        var provider = Substitute.For<IMcpToolProvider>();
        provider.ConnectAsync(
                Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(empty);
        provider.ConnectAsync(
                Arg.Is<IReadOnlySet<string>?>(selection => selection != null && selection.Contains("cockpit-coding")),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(coding);
        return provider;
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
        var arguments = args.ToDictionary(pair => pair.Key, pair => pair.Value);
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent($"call_{name}", name, arguments)],
        };

        await Task.CompletedTask;
    }

    private static Task<List<SessionEvent>> _CollectUntilTurnCompletedAsync(ISessionDriver driver) =>
        _CollectUntilAsync(driver, evt => evt is TurnCompleted);

    private static async Task<List<SessionEvent>> _CollectUntilAsync(ISessionDriver driver, Func<SessionEvent, bool> until)
    {
        var events = new List<SessionEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var evt in driver.Events.WithCancellation(cts.Token))
        {
            events.Add(evt);
            if (until(evt))
            {
                break;
            }
        }

        return events;
    }
}
