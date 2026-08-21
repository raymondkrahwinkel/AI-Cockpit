using Microsoft.Extensions.AI;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// The host-run tool loop a plugin provider can ask for (AC-964): what it connects with, and — the part where a
/// mistake is a permission hole rather than a bug — that every call it offers is decided host-side first.
/// </summary>
public class PluginHostToolLoopTests
{
    [Fact]
    public async Task ToolCall_TheOperatorDenies_DoesNotRunTheToolAndReportsTheRefusal()
    {
        var ran = false;
        var tool = AIFunctionFactory.Create(() => { ran = true; return "did the thing"; }, "write_file");
        await using var session = await _StartAsync(PluginHostToolLoop.ToolsAndSearch, tool);

        // A plugin driver never decides this: it can only ask. The prompt has to reach the operator, and the
        // answer has to be what runs or refuses the call — nothing the plugin says can shortcut either half.
        var call = session.Toolset.InvokeAsync("write_file", "{}");
        var prompt = await session.NextAsync<PermissionRequested>();
        session.Toolset.Gate.Respond(prompt.ToolUseId, allow: false);

        Assert.Equal("Tool call was denied.", await call);
        Assert.False(ran);
    }

    [Fact]
    public async Task ToolCall_TheOperatorAllows_RunsTheToolAndReportsItsResult()
    {
        var tool = AIFunctionFactory.Create((string path) => $"read {path}", "read_file");
        await using var session = await _StartAsync(PluginHostToolLoop.ToolsAndSearch, tool);

        var call = session.Toolset.InvokeAsync("read_file", """{"path":"/tmp/x"}""");
        var prompt = await session.NextAsync<PermissionRequested>();
        session.Toolset.Gate.Respond(prompt.ToolUseId, allow: true);

        Assert.Equal("read /tmp/x", await call);
    }

    [Fact]
    public async Task ToolCall_OnADelegatedSession_IsDecidedAgainstTheCeilingWithoutPrompting()
    {
        var ran = false;
        var tool = AIFunctionFactory.Create(() => { ran = true; return "wrote"; }, "write_file");
        await using var session = await _StartAsync(
            PluginHostToolLoop.ToolsAndSearch,
            new Dictionary<string, ToolPermissionClass> { ["write_file"] = ToolPermissionClass.Write },
            tool);

        // AC-79: a delegated run has nobody to answer a prompt, so its ceiling is what decides. Before the adapter
        // forwarded SetDelegatedToolGateAsync this was a no-op on this route — the call would have hung on a prompt
        // no one could see, and a ceiling that binds nothing is the hole this pins shut.
        await session.Driver.SetDelegatedToolGateAsync(ceiling: string.Empty, allowedTools: []);

        // Raced against a deadline: without the ceiling reaching the gate this call waits on a prompt nobody can
        // answer, and a hang would leave the mutation looking survivable rather than caught.
        var result = await _WithinFiveSecondsAsync(session.Toolset.InvokeAsync("write_file", "{}"));

        Assert.False(ran);
        Assert.Contains("write", result, StringComparison.OrdinalIgnoreCase);

        // Read up to the result the refusal produced, so "no prompt" is measured over everything the call
        // emitted rather than over an empty buffer nothing had been read from yet.
        await session.NextAsync<ToolResult>();
        Assert.Empty(session.Seen<PermissionRequested>());
    }

    [Fact]
    public async Task ToolCall_TheTranscriptCarriesTheCallAndItsResultUnderOneId()
    {
        var tool = AIFunctionFactory.Create(() => "ok", "read_file");
        await using var session = await _StartAsync(PluginHostToolLoop.ToolsAndSearch, tool);

        var call = session.Toolset.InvokeAsync("read_file", "{}");
        var prompt = await session.NextAsync<PermissionRequested>();
        session.Toolset.Gate.Respond(prompt.ToolUseId, allow: true);
        await call;

        // The row, the prompt and the result share one id, which is what the transcript pairs them on. Two ids
        // (one from the plugin, one from the gate) would leave the prompt hanging off no row at all.
        var result = await session.NextAsync<ToolResult>();
        var use = Assert.Single(session.Seen<ToolUseRequested>());
        Assert.Equal(use.ToolUseId, prompt.ToolUseId);
        Assert.Equal(use.ToolUseId, result.ToolUseId);
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task Tools_BelowTheThreshold_AreAllOfferedWithNoSearchProxies()
    {
        await using var session = await _StartAsync(PluginHostToolLoop.ToolsAndSearch, _Tools(3));

        Assert.Equal(3, session.Toolset.Tools.Count);
        Assert.DoesNotContain(session.Toolset.Tools, tool => tool.Name is CockpitToolSearch.SearchToolName);
    }

    [Fact]
    public async Task Tools_AboveTheThreshold_MoveBehindTheSearchProxies()
    {
        await using var session = await _StartAsync(PluginHostToolLoop.ToolsAndSearch, _Tools(CockpitToolSearch.PreloadThreshold + 1));

        Assert.Contains(session.Toolset.Tools, tool => tool.Name == CockpitToolSearch.SearchToolName);
        Assert.Contains(session.Toolset.Tools, tool => tool.Name == CockpitToolSearch.CallToolName);

        // The count the header shows stays the real one: everything reachable, plus the two proxies it is
        // reachable through.
        Assert.Equal(CockpitToolSearch.PreloadThreshold + 3, session.Toolset.ReachableToolNames.Count);
    }

    [Fact]
    public async Task Tools_ForAProviderWithItsOwnSearch_NeverGetOurs()
    {
        await using var session = await _StartAsync(PluginHostToolLoop.ToolsOnly, _Tools(CockpitToolSearch.PreloadThreshold + 1));

        // Raymond's condition on this ticket: a provider that brings a tool search of its own must not also be
        // handed the cockpit's, however large the catalogue gets. Two ways to do one thing confuses the model and
        // spends the very prompt budget the search layer exists to win back.
        Assert.DoesNotContain(session.Toolset.Tools, tool => tool.Name is CockpitToolSearch.SearchToolName or CockpitToolSearch.CallToolName);
        Assert.Equal(CockpitToolSearch.PreloadThreshold + 1, session.Toolset.Tools.Count);
    }

    [Fact]
    public async Task StartAsync_ConnectsWithThePaneConfinementAndProjectTheHostResolved()
    {
        string? pane = null;
        string? confine = null;
        string? project = null;
        var toolProvider = _ToolProvider([], new Dictionary<string, ToolPermissionClass>());

        // Discarded rather than awaited: this is NSubstitute recording what to capture, not a call to make.
        _ = toolProvider.ConnectAsync(
            Arg.Any<IReadOnlySet<string>?>(),
            Arg.Do<string?>(value => pane = value),
            Arg.Do<string?>(value => confine = value),
            Arg.Do<string?>(value => project = value),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        var driver = _Adapter(new StubPluginDriver(), PluginHostToolLoop.ToolsAndSearch, toolProvider);
        await driver.StartAsync(
            workingDirectory: "/tmp/worktree",
            launchOptions: new Dictionary<string, string>
            {
                [WellKnownPluginSessionOptions.PaneId] = "pane-7",
                [WellKnownPluginSessionOptions.ConfineFileToolsToWorkingDirectory] = "true",
            },
            projectId: "project-3");

        // Criterion 2: the same call the built-in driver makes, so the per-session token (AC-89), worktree
        // confinement (AC-174) and project scoping (AC-218) hold on this route without a second implementation.
        Assert.Equal("pane-7", pane);
        Assert.Equal("/tmp/worktree", confine);
        Assert.Equal("project-3", project);
    }

    [Fact]
    public async Task StartAsync_ForAProviderThatDeclaredNoHostLoop_MountsNothing()
    {
        var toolProvider = _ToolProvider([], new Dictionary<string, ToolPermissionClass>());
        var inner = new StubPluginDriver();
        var driver = _Adapter(inner, PluginHostToolLoop.None, toolProvider);

        // Criterion 9: every already-published plugin defaults to None and must behave exactly as before —
        // no connection made on its behalf, and no toolset handed to a driver that would not know what to do with one.
        await driver.StartAsync();

        await toolProvider.DidNotReceive().ConnectAsync(Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.Null(inner.Toolset);
    }

    private static async Task<string> _WithinFiveSecondsAsync(Task<string> call)
    {
        if (await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(5))) != call)
        {
            throw new TimeoutException("The tool call was still waiting on a decision after 5s — nothing decided it.");
        }

        return await call;
    }

    [Fact]
    public async Task StartAsync_WithAHostToolLoop_HandsTheDriverNoServersToMountItself()
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([new McpServerConfig { Name = "filesystem", Transport = McpTransport.Stdio, Command = "npx" }]);

        var withLoop = new StubPluginDriver();
        await _AdapterWithCatalog(withLoop, PluginHostToolLoop.ToolsAndSearch, catalog).StartAsync();

        var withoutLoop = new StubPluginDriver();
        await _AdapterWithCatalog(withoutLoop, PluginHostToolLoop.None, catalog).StartAsync();

        // The endpoints and the toolset are alternatives, never both: the host already connected these servers for
        // the first driver, and handing them over as well would start every stdio server a second time.
        Assert.Empty(withLoop.McpServers ?? []);
        Assert.NotNull(withLoop.Toolset);
        Assert.Single(withoutLoop.McpServers ?? []);
        Assert.Null(withoutLoop.Toolset);
    }

    private static ISessionDriver _AdapterWithCatalog(StubPluginDriver inner, PluginHostToolLoop loop, IMcpServerCatalog catalog) =>
        new PluginSessionDriverAdapter(
            inner,
            new PluginSessionCapabilities(SupportsTools: false, SupportsPermissions: false) { HostToolLoop = loop },
            new McpAuthKey(),
            catalog,
            mcpToolProvider: _ToolProvider([], new Dictionary<string, ToolPermissionClass>()));

    private static AIFunction[] _Tools(int count) =>
        [.. Enumerable.Range(0, count).Select(index => AIFunctionFactory.Create(() => "ok", $"tool_{index}"))];

    private static IMcpToolProvider _ToolProvider(AIFunction[] tools, IReadOnlyDictionary<string, ToolPermissionClass> toolClasses)
    {
        var toolSession = Substitute.For<IMcpToolSession>();
        toolSession.Tools.Returns([.. tools.Select(tool => new McpSessionTool(tool, "test-server", AlwaysMounted: false))]);
        toolSession.ConnectedServerNames.Returns(tools.Length == 0 ? [] : new[] { "test-server" });
        toolSession.ServersNeedingSignIn.Returns([]);
        toolSession.ToolClasses.Returns(toolClasses);

        var toolProvider = Substitute.For<IMcpToolProvider>();
        toolProvider.ConnectAsync(Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(toolSession);
        return toolProvider;
    }

    private static ISessionDriver _Adapter(StubPluginDriver inner, PluginHostToolLoop loop, IMcpToolProvider toolProvider) =>
        new PluginSessionDriverAdapter(
            inner,
            new PluginSessionCapabilities(SupportsTools: false, SupportsPermissions: false) { HostToolLoop = loop },
            new McpAuthKey(),
            mcpToolProvider: toolProvider);

    private static Task<StartedSession> _StartAsync(PluginHostToolLoop loop, params AIFunction[] tools) =>
        _StartAsync(loop, new Dictionary<string, ToolPermissionClass>(), tools);

    private static async Task<StartedSession> _StartAsync(PluginHostToolLoop loop, IReadOnlyDictionary<string, ToolPermissionClass> toolClasses, params AIFunction[] tools)
    {
        var inner = new StubPluginDriver();
        var driver = _Adapter(inner, loop, _ToolProvider(tools, toolClasses));
        await driver.StartAsync();

        // Enumerated after start: both streams are buffered channels, so nothing published in between is lost,
        // and the merged enumerator then reads the toolset the adapter has actually mounted.
        return new StartedSession(driver, inner.Toolset as HostPluginToolset ?? throw new InvalidOperationException("The adapter mounted no toolset."), driver.Events.GetAsyncEnumerator());
    }

    // A started session plus a live reader on its event stream, so a test can wait for the prompt a call raises
    // rather than poll for it.
    private sealed class StartedSession(ISessionDriver driver, HostPluginToolset toolset, IAsyncEnumerator<SessionEvent> events) : IAsyncDisposable
    {
        private readonly List<SessionEvent> _seen = [];

        public ISessionDriver Driver => driver;

        public HostPluginToolset Toolset => toolset;

        public async Task<T> NextAsync<T>() where T : SessionEvent
        {
            foreach (var seen in _seen.OfType<T>())
            {
                return seen;
            }

            // Raced against a deadline rather than awaited outright: an event that never arrives would otherwise
            // hang the whole run, and a hang is not a usable red — it measures something other than the assertion.
            while (true)
            {
                var read = events.MoveNextAsync().AsTask();
                if (await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5))) != read)
                {
                    throw new TimeoutException($"No {typeof(T).Name} within 5s. Seen: {string.Join(", ", _seen.Select(seen => seen.GetType().Name))}.");
                }

                if (!await read)
                {
                    throw new InvalidOperationException($"The session ended before a {typeof(T).Name}.");
                }

                _seen.Add(events.Current);
                if (events.Current is T match)
                {
                    return match;
                }
            }
        }

        // What this session has already produced, as read so far. Never reads ahead: a second MoveNextAsync
        // while one is outstanding is undefined on an async iterator, and it crashed the host when it was tried.
        public IReadOnlyList<T> Seen<T>() where T : SessionEvent => [.. _seen.OfType<T>()];

        public async ValueTask DisposeAsync()
        {
            await driver.DisposeAsync();
            await events.DisposeAsync();
        }
    }

    // The narrowest possible plugin driver: it keeps whatever toolset the host hands it and does nothing else,
    // so these tests measure the host side rather than any provider's loop.
    private sealed class StubPluginDriver : IPluginSessionDriver
    {
        private readonly PluginSessionEventPublisher _events = new();

        public IPluginToolset? Toolset { get; private set; }

        public IReadOnlyList<PluginMcpServer>? McpServers { get; private set; }

        public PluginSessionCapabilities Capabilities { get; } = new(SupportsTools: false, SupportsPermissions: false);

        public string? SessionId { get; private set; }

        public IAsyncEnumerable<PluginSessionEvent> Events => _events.Events;

        public Task StartAsync(string? model = null, CancellationToken cancellationToken = default)
        {
            SessionId = "stub-session";
            return Task.CompletedTask;
        }

        public Task StartAsync(string? model, string? workingDirectory, string? resumeSessionId, IReadOnlyDictionary<string, string>? options, IReadOnlyList<PluginMcpServer>? mcpServers, IReadOnlyDictionary<string, string>? environment, IPluginToolset? toolset, CancellationToken cancellationToken)
        {
            Toolset = toolset;
            McpServers = mcpServers;
            return StartAsync(model, cancellationToken);
        }

        public Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _events.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
