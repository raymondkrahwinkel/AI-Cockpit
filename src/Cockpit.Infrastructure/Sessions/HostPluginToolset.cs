using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// The host side of a plugin driver's tool loop (AC-964): it connects this session's MCP servers, gates every
// call, writes the transcript's tool rows, and hands the plugin nothing but names, schemas and one invoke. A
// plugin that runs the loop therefore cannot widen what the session may do, only choose when to call.
internal sealed class HostPluginToolset : IPluginToolset, IAsyncDisposable
{
    private readonly IMcpToolSession _toolSession;
    private IReadOnlyList<AIFunction> _turnTools;
    private IReadOnlyList<McpSessionTool> _gatedTools;
    private readonly PluginHostToolLoop _loop;
    private readonly ILogger? _logger;
    private int _toolsChanged;

    private HostPluginToolset(IMcpToolSession toolSession, PluginHostToolLoop loop, ILogger? logger, SessionToolApprovalGate gate, PluginSessionEventPublisher events)
    {
        _toolSession = toolSession;
        _loop = loop;
        _logger = logger;
        Gate = gate;
        Events = events;
        _gatedTools = _LiveGatedTools();
        _turnTools = _BuildTurnTools(_gatedTools, loop, logger);
        _toolSession.ToolsChanged += _OnToolsChanged;
    }

    private void _OnToolsChanged() => Interlocked.Exchange(ref _toolsChanged, 1);

    private IReadOnlyList<McpSessionTool> _LiveGatedTools()
    {
        Gate.ToolClasses = _toolSession.ToolClasses;
        return [.. _toolSession.Tools.Select(tool => tool with { Function = new GatedTool(tool.Function, Gate) })];
    }

    public void RefreshTools()
    {
        if (Interlocked.Exchange(ref _toolsChanged, 0) != 0)
        {
            _gatedTools = _LiveGatedTools();
            _turnTools = _BuildTurnTools(_gatedTools, _loop, _logger);
        }
    }

    public IReadOnlyList<PluginToolDescriptor> Tools => [.. _turnTools.Select(tool => new PluginToolDescriptor(_ServerOf(_gatedTools, tool.Name), tool.Name, tool.Description, tool.JsonSchema.GetRawText()))];

    public IReadOnlyList<string> ReachableToolNames =>
        [
            .. _gatedTools.Select(tool => tool.Function.Name),
            .. _turnTools.Select(tool => tool.Name).Where(name => name is CockpitToolSearch.SearchToolName or CockpitToolSearch.CallToolName),
        ];

    // The gate every call goes through, and what the adapter answers a permission prompt on.
    public SessionToolApprovalGate Gate { get; }

    public PluginSessionEventPublisher Events { get; }

    // The servers that really answered, for the header that names this session's mounts (AC-927).
    public IReadOnlyList<string> ConnectedServerNames => _toolSession.ConnectedServerNames;

    // The servers that did not, with why (AC-997) — for the same header, so a fallen-over server is not just an
    // absence from the count above.
    public IReadOnlyList<McpServerConnectionIssue> ConnectionIssues => _toolSession.ConnectionIssues;

    // The pane token ConnectAsync minted below, so the adapter can hand its plugin the same live token
    // instead of minting a second one for the same pane (AC-994).
    public string? PaneToken => _toolSession.PaneToken;

    // Connects the session's servers and wraps each tool in its GatedTool, exactly as the built-in
    // OpenAiCompatSessionDriver does — same ConnectAsync call, so the per-session token (AC-89), worktree
    // confinement (AC-174) and project scoping (AC-218) all apply here too.
    public static async Task<HostPluginToolset> ConnectAsync(
        IMcpToolProvider toolProvider,
        PluginHostToolLoop loop,
        IReadOnlySet<string>? selection,
        string? paneId,
        string? confineRoot,
        string? projectId,
        string? workingDirectory,
        Func<string?> sessionId,
        ILogger? logger,
        CancellationToken cancellationToken,
        string? agentName = null)
    {
        var toolSession = await toolProvider.ConnectAsync(selection, paneId, confineRoot, projectId, workingDirectory, cancellationToken, agentName).ConfigureAwait(false);

        var selectionText = selection is null ? "(no restriction)" : $"[{string.Join(", ", selection)}]";
        if (toolSession.ConnectedServerNames.Count == 0 && selection is { Count: > 0 })
        {
            logger?.LogWarning("Plugin-provider MCP fan-out connected no servers from selection {Selection}; the session starts with none.", selectionText);
        }
        else
        {
            logger?.LogInformation(
                "Plugin-provider MCP fan-out: {Count} server(s) [{Names}] from selection {Selection}.",
                toolSession.ConnectedServerNames.Count,
                string.Join(", ", toolSession.ConnectedServerNames),
                selectionText);
        }

        // AC-500: a server the registry declared OAuth is a named outcome distinct from an ordinary connect
        // failure, reported here at the one place this route reports its fan-out.
        if (toolSession.ServersNeedingSignIn.Count > 0)
        {
            logger?.LogWarning(
                "Plugin-provider MCP fan-out: [{Names}] need an OAuth sign-in and were skipped — no tools from them.",
                string.Join(", ", toolSession.ServersNeedingSignIn));
        }

        // The transcript rows for this session's tool calls are written here, not by the plugin: the gate owns the
        // tool-use id the permission prompt is answered on, and a second id from across the boundary would leave
        // the prompt attached to no row at all.
        var events = new PluginSessionEventPublisher();
        var gate = new SessionToolApprovalGate(
            (toolUseId, toolName, inputJson) => events.Publish(new PluginToolUseRequested { SessionId = sessionId(), ToolUseId = toolUseId, ToolName = toolName, InputJson = inputJson }),
            (toolUseId, toolName, inputJson) => events.Publish(new PluginPermissionRequested { SessionId = sessionId(), ToolUseId = toolUseId, ToolName = toolName, InputJson = inputJson }),
            (toolUseId, content, isError) => events.Publish(new PluginToolResult { SessionId = sessionId(), ToolUseId = toolUseId, Content = content, IsError = isError }))
        {
            ToolClasses = toolSession.ToolClasses,
        };

        return new HostPluginToolset(toolSession, loop, logger, gate, events);
    }

    // What rides along in the model's tool list every turn (AC-963/AC-964). ToolsOnly keeps the whole catalogue
    // preloaded whatever its size: the search proxies are only ever added for a provider that said it has no
    // tool search of its own, so the model is never handed two ways to find the same tool.
    private List<AIFunction> _BuildTurnTools(IReadOnlyList<McpSessionTool> gatedTools, PluginHostToolLoop loop, ILogger? logger)
    {
        if (loop != PluginHostToolLoop.ToolsAndSearch || gatedTools.Count <= CockpitToolSearch.PreloadThreshold)
        {
            return [.. gatedTools.Select(tool => tool.Function)];
        }

        var preloaded = gatedTools.Where(tool => tool.AlwaysMounted || CodingTools.StaysNative(tool.ServerName)).ToList();
        var searchable = gatedTools.Except(preloaded).ToList();
        logger?.LogInformation(
            "Tool search mode: {Preloaded} always-mounted tool(s) preloaded, {Searchable} behind {SearchTool} — about {Tokens} tokens of schema kept out of every request.",
            preloaded.Count,
            searchable.Count,
            CockpitToolSearch.SearchToolName,
            McpToolTokenMath.Format(McpToolTokenMath.EstimateTokens(searchable.Select(tool => McpToolTokenEstimator.SerialiseForEstimate(tool.Function)))));

        return [.. preloaded.Select(tool => tool.Function), .. CockpitToolSearch.Build(_LiveGatedTools).OfType<AIFunction>()];
    }

    // The search proxies belong to no server; the two names the App matches on are how it recognises them.
    private static string _ServerOf(IReadOnlyList<McpSessionTool> gatedTools, string toolName) =>
        gatedTools.FirstOrDefault(tool => string.Equals(tool.Function.Name, toolName, StringComparison.Ordinal))?.ServerName ?? CockpitToolSearch.ProxyServerName;

    public async Task<string> InvokeAsync(string name, string argumentsJson, CancellationToken cancellationToken = default)
    {
        var tool = _turnTools.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal))
            ?? _gatedTools.FirstOrDefault(candidate => string.Equals(candidate.Function.Name, name, StringComparison.Ordinal))?.Function;
        if (tool is null)
        {
            return $"No tool called \"{name}\" is available to this session.";
        }

        var arguments = _ParseArguments(argumentsJson);
        if (arguments is null)
        {
            return $"The arguments for \"{name}\" must be a JSON object of the tool's parameters.";
        }

        // The gated function, so approval, tool class and the AC-79 ceiling are decided here rather than by
        // whatever plugin asked. A refusal comes back as the gate's own text — the tool result the model sees.
        var result = await tool.InvokeAsync(new AIFunctionArguments(arguments), cancellationToken).ConfigureAwait(false);
        return GatedTool.TextOf(result);
    }

    private static Dictionary<string, object?>? _ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Object => document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone()),
                JsonValueKind.Null or JsonValueKind.Undefined => [],
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _toolSession.ToolsChanged -= _OnToolsChanged;
        Gate.CancelPending();
        Events.TryComplete();
        await _toolSession.DisposeAsync().ConfigureAwait(false);
    }
}
