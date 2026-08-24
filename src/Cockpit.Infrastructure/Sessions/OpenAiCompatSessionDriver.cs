using System.ClientModel;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// `ISessionDriver` for the local OpenAI-compatible providers (Ollama, LM Studio) via Microsoft.Extensions.AI's
// `IChatClient`; streams text and owns conversation history itself (HTTP is stateless), and when MCP servers
// are registered runs an agentic tool-loop (#26) gated through PermissionRequested; Claude-CLI-only controls (permission mode, thinking) stay no-ops.
internal sealed class OpenAiCompatSessionDriver : ISessionDriver, ITransientService
{
    private readonly IChatClientFactory _chatClientFactory;
    private readonly IMcpToolProvider _mcpToolProvider;
    private readonly ILogger<OpenAiCompatSessionDriver> _logger;
    private readonly SessionMcpMounts? _mcpMounts;

    private readonly Channel<SessionEvent> _events = Channel.CreateUnbounded<SessionEvent>();
    private readonly List<ChatMessage> _history = [];

    private IChatClient? _agent;
    private IMcpToolSession? _toolSession;

    // Every mounted tool, wrapped in its `GatedTool` — the catalogue `search_tools` reads and `call_tool` runs
    // against (AC-963), and the list of what this session can reach at all.
    private List<McpSessionTool> _gatedTools = [];

    // What actually rides along in `ChatOptions.Tools` each turn: the whole catalogue below the threshold, and
    // above it only the always-mounted endpoints plus the two `cockpit-tools` proxies.
    private List<AITool> _turnTools = [];
    private string? _model;
    private string? _sessionId;
    private CancellationTokenSource? _turnCancellation;

    // AC-132: set once a turn makes a tool call, so a turn with no text and no tool activity surfaces as "no
    // response" instead of a silent success. Reset at each turn start; volatile because the tool loop can run
    // the call on a pool thread even though turns themselves are serialised.
    private volatile bool _turnHadToolActivity;

    // The shared approval gate (AC-964): the same decision path the plugin-provider tool loop runs, so the one
    // place a mistake would be a permission hole exists once. A tool call means this turn produced something
    // visible even if it ends with no assistant text, so the no-response vangnet must not fire for it (AC-132).
    private readonly SessionToolApprovalGate _gate;

    public OpenAiCompatSessionDriver(IChatClientFactory chatClientFactory, IMcpToolProvider mcpToolProvider, ILogger<OpenAiCompatSessionDriver> logger, SessionMcpMounts? mcpMounts = null)
    {
        _chatClientFactory = chatClientFactory;
        _mcpToolProvider = mcpToolProvider;
        _logger = logger;
        _mcpMounts = mcpMounts;
        _gate = new SessionToolApprovalGate(
            (toolUseId, toolName, inputJson) =>
            {
                _turnHadToolActivity = true;
                _events.Writer.TryWrite(new ToolUseRequested { SessionId = _sessionId, ToolUseId = toolUseId, ToolName = toolName, InputJson = inputJson });
            },
            (toolUseId, toolName, inputJson) => _events.Writer.TryWrite(new PermissionRequested { SessionId = _sessionId, ToolUseId = toolUseId, ToolName = toolName, InputJson = inputJson }),
            (toolUseId, content, isError) => _events.Writer.TryWrite(new ToolResult { SessionId = _sessionId, ToolUseId = toolUseId, Content = content, IsError = isError }));
    }

    // Tool support flips true once MCP servers connect; permission mode/model switch/thinking stay off since the
    // local model is fixed by its profile. SupportsVision stays false too — SendUserMessageAsync ignores images
    // entirely, so advertising vision here would be the dead promise #64 exists to prevent (fase 2 adds it for real).
    public SessionCapabilities Capabilities { get; private set; } = new(
        SupportsTools: false,
        SupportsPermissions: false,
        SupportsLiveModelSwitch: false,
        SupportsPlanMode: false,
        SupportsThinking: false,
        SupportsVision: false);

    public string? SessionId => _sessionId;

    public SessionProfile? Profile { get; private set; }

    public IAsyncEnumerable<SessionEvent> Events => _events.Reader.ReadAllAsync();

    // A built-in HTTP chat provider declares no per-session launch options of its own, but it does read the ones the
    // host sets: the pane id it connects its tool loop on, the confinement flag, and the system prompt to append.
    public async Task StartAsync(SessionProfile? profile = null, string? permissionMode = null, string? model = null, IReadOnlySet<string>? enabledMcpServerNames = null, string? workingDirectory = null, SessionResume? resume = null, IReadOnlyDictionary<string, string>? launchOptions = null, string? projectId = null, CancellationToken cancellationToken = default)
    {
        // workingDirectory is used only to confine file tools (below): a local session talks HTTP to a model server with
        // no cwd, but its file access rides MCP servers, so an isolated run confines those to this directory instead.
        var config = profile?.ProviderConfig
            ?? throw new InvalidOperationException($"{nameof(OpenAiCompatSessionDriver)} requires a profile with an OpenAI-compatible provider config.");

        Profile = profile;
        _model = string.IsNullOrWhiteSpace(model) ? _ModelFrom(config) : model;
        _sessionId = Guid.NewGuid().ToString();

        // AC-192: .Use* registration order = outer→inner, so below is UseFunctionInvocation (outer) →
        // HermesToolCallChatClient (middle) → model client (inner). The Hermes shim turns local-model text
        // tool-calls into structured FunctionCallContent before UseFunctionInvocation sees them; already-structured calls pass through.
        _agent = new ChatClientBuilder(_chatClientFactory.Create(config))
            .UseFunctionInvocation()
            .Use(inner => new HermesToolCallChatClient(inner))
            .Build();
        // AC-89: pass this session's pane id (the App sets it as the cockpit.pane-id launch option) so the tool loop
        // connects to the cockpit endpoints on a per-session token — the consent broker then scopes on this pane, not
        // the id the local model declares.
        var paneId = launchOptions is not null && launchOptions.TryGetValue(WellKnownPluginSessionOptions.PaneId, out var value) ? value : null;

        // AC-174 (Raymond 2026-07-22): confine file tools to the working directory when the host isolates this
        // session in a worktree — re-root the filesystem server and drop every escape channel. Only a session
        // actually confined here may vouch the capability below; the flag alone is never enough.
        var confineRoot = launchOptions is not null
            && launchOptions.TryGetValue(WellKnownPluginSessionOptions.ConfineFileToolsToWorkingDirectory, out var confineFlag)
            && string.Equals(confineFlag, "true", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(workingDirectory)
                ? workingDirectory
                : null;

        // #44/AC-130: a programmatic launch (a plugin/workflow shortcut, a restored session) carries no dialog-built
        // selection, so fall back to the profile's saved one rather than reaching every enabled server — the same
        // fix the plugin-driver adapter applies, so the local-model tool loop honours the checklist too.
        var selection = McpServerRegistryFilter.EffectiveSessionSelection(enabledMcpServerNames, profile?.EnabledMcpServerNames);
        _toolSession = await _mcpToolProvider.ConnectAsync(selection, paneId, confineRoot, projectId, workingDirectory, cancellationToken).ConfigureAwait(false);

        // Symmetric with the plugin-driver adapter (#44): say which servers the tool loop connected and against
        // which selection, so a local-model session missing its MCP servers is a log line rather than a silent
        // gap; a non-empty selection that connected nothing is surfaced at Warning.
        var selectionText = selection is null ? "(no restriction)" : $"[{string.Join(", ", selection)}]";
        if (_toolSession.ConnectedServerNames.Count == 0 && selection is { Count: > 0 })
        {
            _logger.LogWarning(
                "Local-model MCP fan-out connected no servers from selection {Selection}; the session starts with none.",
                selectionText);
        }
        else
        {
            _logger.LogInformation(
                "Local-model MCP fan-out: {Count} server(s) [{Names}] from selection {Selection}.",
                _toolSession.ConnectedServerNames.Count,
                string.Join(", ", _toolSession.ConnectedServerNames),
                selectionText);
        }

        // AC-927: the servers that really answered, so the header names those rather than the checklist this
        // session was launched from — the one route where a missing server is otherwise invisible to the operator.
        if (paneId is { Length: > 0 })
        {
            _mcpMounts?.Report(paneId, _toolSession.ConnectedServerNames, _toolSession.ConnectionIssues);
        }

        // AC-500: a server the plugin/registry declared OAuth is a named outcome distinct from an ordinary
        // connect failure — reported once here, at the one place this driver already reports its MCP fan-out,
        // rather than left as session state nothing ever reads.
        if (_toolSession.ServersNeedingSignIn.Count > 0)
        {
            _logger.LogWarning(
                "Local-model MCP fan-out: [{Names}] need an OAuth sign-in and were skipped — no tools from them.",
                string.Join(", ", _toolSession.ServersNeedingSignIn));
        }

        _gate.ToolClasses = _toolSession.ToolClasses;
        _gatedTools = [.. _toolSession.Tools.Select(tool => tool with { Function = new GatedTool(tool.Function, _gate) })];
        _turnTools = _BuildTurnTools();
        // SupportsTools flips true once servers connected. ConfinesFileAccessToWorkingDirectory is vouched only when
        // confineRoot re-rooted file access and dropped every escape channel — the host's fail-closed isolation gate
        // reads this, so it must never read true for a session that was not actually confined.
        Capabilities = Capabilities with
        {
            SupportsTools = _turnTools.Count > 0,
            ConfinesFileAccessToWorkingDirectory = confineRoot is not null,
        };

        // Seed the conversation with the profile's base system prompt plus any hidden per-session prompt the host
        // folded into the options map (AC-180 — an embedded run's brief, e.g. Autopilot's CEO), so every turn carries
        // them (HTTP is stateless — the client owns the history, so a system message once at the front is enough).
        var systemPrompt = _CombineSystemPrompt(_SystemPromptFrom(config), launchOptions);
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            _history.Add(new ChatMessage(ChatRole.System, systemPrompt));
        }

        // Report the actual tool names (not server names) so the session's "N tools" count is real and verifiable —
        // e.g. confirming read_file is actually available. In search mode the catalogue rides through `call_tool`
        // rather than preloaded, so this still names every reachable tool plus the two proxies it gets there with.
        _events.Writer.TryWrite(new SessionInitialized
        {
            SessionId = _sessionId,
            Cwd = string.Empty,
            Tools = [.. _gatedTools.Select(tool => tool.Function.Name), .. _turnTools.Select(tool => tool.Name).Where(name => name is CockpitToolSearch.SearchToolName or CockpitToolSearch.CallToolName)],
        });
    }

    // What rides along in `ChatOptions.Tools` every turn (AC-963). Above the threshold the schemas are the problem,
    // so only the always-mounted endpoints stay native: `cockpit-session set_status` is plumbing every session must
    // reach without going looking for it first.
    private List<AITool> _BuildTurnTools()
    {
        if (_gatedTools.Count <= CockpitToolSearch.PreloadThreshold)
        {
            return [.. _gatedTools.Select(tool => (AITool)tool.Function)];
        }

        var preloaded = _gatedTools.Where(tool => tool.AlwaysMounted).ToList();
        var searchable = _gatedTools.Except(preloaded).ToList();
        _logger.LogInformation(
            "Tool search mode: {Preloaded} always-mounted tool(s) preloaded, {Searchable} behind {SearchTool} — about {Tokens} tokens of schema kept out of every request.",
            preloaded.Count,
            searchable.Count,
            CockpitToolSearch.SearchToolName,
            McpToolTokenMath.Format(McpToolTokenMath.EstimateTokens(searchable.Select(tool => McpToolTokenEstimator.SerialiseForEstimate(tool.Function)))));

        return [.. preloaded.Select(tool => (AITool)tool.Function), .. CockpitToolSearch.Build(_gatedTools)];
    }

    public Task SendUserMessageAsync(string text, IReadOnlyList<ImageAttachment>? images = null, CancellationToken cancellationToken = default)
    {
        if (_agent is null)
        {
            throw new InvalidOperationException($"{nameof(SendUserMessageAsync)} called before {nameof(StartAsync)}.");
        }

        // Run the turn in the background so the caller returns immediately and consumes the reply through
        // Events, mirroring how the Claude-CLI driver's send returns before the response streams back.
        _turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = _RunTurnAsync(text, _turnCancellation.Token);
        return Task.CompletedTask;
    }

    private async Task _RunTurnAsync(string text, CancellationToken cancellationToken)
    {
        _history.Add(new ChatMessage(ChatRole.User, text));
        var toolsAvailable = _turnTools.Count > 0;
        _turnHadToolActivity = false;

        try
        {
            await _StreamTurnAsync(toolsAvailable ? _turnTools : null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A genuine user interrupt: the token this turn runs under was cancelled, so keep a clean "interrupted"
            // turn with no error row. An OperationCanceledException the SDK throws while aborting on a non-2xx response
            // leaves the token uncancelled, so it fails this filter and falls through as an error instead (AC-132).
            _events.Writer.TryWrite(new TurnCompleted { SessionId = _sessionId, Subtype = "interrupted", Result = null, IsError = false, StopReason = "interrupt" });
        }
        catch (Exception ex)
        {
            var message = _DescribeError(ex);

            // AC-135: some local runtimes can't tool-call at all (a GGUF chat-template whose tool-parser fails once
            // `tools` are sent) — retry once with no tools instead of failing the turn. The note streams as assistant
            // text, not SessionError, since a SessionError ends the turn and could let a second turn start mid-retry.
            if (toolsAvailable && _IsToolTemplateError(message))
            {
                _logger.LogWarning(ex, "Local model rejected tools ({Message}); retrying without tools", message);
                _events.Writer.TryWrite(new AssistantTextDelta
                {
                    SessionId = _sessionId,
                    BlockIndex = 0,
                    Text = "_(This model does not support tool-calling in this runtime — answering without tools. Turn off this profile's MCP servers to stop offering them.)_\n\n",
                });
                // The retry sends no tools, so it can raise no tool activity; clear any flag the failed attempt set
                // so the retry's own no-response check is not suppressed.
                _turnHadToolActivity = false;

                try
                {
                    await _StreamTurnAsync(tools: null, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _events.Writer.TryWrite(new TurnCompleted { SessionId = _sessionId, Subtype = "interrupted", Result = null, IsError = false, StopReason = "interrupt" });
                }
                catch (Exception retryEx)
                {
                    _EmitError(retryEx);
                }

                return;
            }

            _logger.LogWarning(ex, "OpenAI-compatible chat request failed: {Message}", message);
            _events.Writer.TryWrite(new SessionError
            {
                SessionId = _sessionId,
                Message = message,
                Exception = ex,
                Kind = _ClassifyError(ex),
                RetryAfter = _RetryAfterFrom(ex),
            });
            _events.Writer.TryWrite(new TurnCompleted { SessionId = _sessionId, Subtype = "error", Result = null, IsError = true });
        }
    }

    // Streams one model turn under `tools` (null = no tools). A turn with no text and no tool call surfaces as
    // "no response" rather than a silent success, and only text-bearing turns are added to history so a blank
    // assistant message never rides along later (AC-132); exceptions propagate for the caller to classify.
    private async Task _StreamTurnAsync(IReadOnlyList<AITool>? tools, CancellationToken cancellationToken)
    {
        var options = new ChatOptions { ModelId = _model, Tools = tools is { Count: > 0 } ? [.. tools] : null };
        var assistant = new StringBuilder();

        await foreach (var update in _agent!.GetStreamingResponseAsync(_history, options, cancellationToken).ConfigureAwait(false))
        {
            var delta = update.Text;
            if (!string.IsNullOrEmpty(delta))
            {
                assistant.Append(delta);
                _events.Writer.TryWrite(new AssistantTextDelta { SessionId = _sessionId, BlockIndex = 0, Text = delta });
            }
        }

        var reply = assistant.ToString();
        var hasText = !string.IsNullOrWhiteSpace(reply);

        if (!hasText && !_turnHadToolActivity)
        {
            _events.Writer.TryWrite(new SessionError { SessionId = _sessionId, Message = "The model returned no response — no text and no tool calls." });
            _events.Writer.TryWrite(new TurnCompleted { SessionId = _sessionId, Subtype = "error", Result = null, IsError = true });
            return;
        }

        // AC-192 no-progress net: a turn ending with an unprocessed Hermes tool-call marker means the parser shim
        // missed an edge and the model's tool request was never executed — surface that as a visible error instead
        // of the silent "success" (and hang) this used to end as, mirroring the no-response net above.
        if (!_turnHadToolActivity && _ContainsUnprocessedToolCallMarker(reply))
        {
            _events.Writer.TryWrite(new SessionError
            {
                SessionId = _sessionId,
                Message = "The model emitted a tool call as text that was not executed, so the run cannot proceed. "
                    + "This local model does not produce tool calls this runtime can run.",
            });
            _events.Writer.TryWrite(new TurnCompleted { SessionId = _sessionId, Subtype = "error", Result = null, IsError = true });
            return;
        }

        if (hasText)
        {
            _history.Add(new ChatMessage(ChatRole.Assistant, reply));
        }

        _events.Writer.TryWrite(new TurnCompleted { SessionId = _sessionId, Subtype = "success", Result = reply, IsError = false });
    }

    private void _EmitError(Exception ex)
    {
        // Read the response body so the operator sees the actual reason (e.g. exceed_context_size_error, or an
        // unparseable tool template) rather than a bare "HTTP 400 (Bad Request)" (AC-132).
        var message = _DescribeError(ex);
        _logger.LogWarning(ex, "OpenAI-compatible chat request failed: {Message}", message);
        _events.Writer.TryWrite(new SessionError
        {
            SessionId = _sessionId,
            Message = message,
            Exception = ex,
            Kind = _ClassifyError(ex),
            RetryAfter = _RetryAfterFrom(ex),
        });
        _events.Writer.TryWrite(new TurnCompleted { SessionId = _sessionId, Subtype = "error", Result = null, IsError = true });
    }

    // Whether a failed request looks like the runtime refusing tool-calling itself (a GGUF tool-parser failure,
    // or "tools unsupported") rather than an ordinary error. Heuristic over the response body (AC-135) since
    // message wording is the only signal a local server gives; only weighed when tools were sent this turn.
    internal static bool _IsToolTemplateError(string message) =>
        _ToolTemplateErrorSignals.Any(signal => message.Contains(signal, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] _ToolTemplateErrorSignals =
    [
        // The LM Studio GGUF-template tool-parser failure (its message also names the "Tool call IDs" rule, but
        // this phrase is the distinctive part). A bare "tool call id" is deliberately not a signal: a server that
        // does support tools can reject a malformed id, which is not a can't-do-tools condition.
        "parser for this template",
        "does not support tool",
        "tools are not supported",
        "tool calling is not supported",
        "tool_use is not supported",
    ];

    // Whether a turn's text still carries a Hermes tool-call marker the parser shim did not convert — an opening
    // `&lt;function=` for a block that never completed, or a stray `&lt;/tool_call&gt;` wrapper. Weighed only
    // when no tool actually ran this turn, so a converted-and-executed call (which leaves no marker) never trips it.
    internal static bool _ContainsUnprocessedToolCallMarker(string text) =>
        text.Contains("<function=", StringComparison.OrdinalIgnoreCase)
        || text.Contains("</tool_call>", StringComparison.OrdinalIgnoreCase);

    // The most useful message for a failed turn: the exception message plus, for a ClientResultException from
    // the OpenAI SDK, the HTTP response body — a local server puts the real reason there (e.g.
    // exceed_context_size_error), which the exception's own "HTTP 400 (Bad Request)" message hides (AC-132).
    internal static string _DescribeError(Exception ex)
    {
        if (ex is ClientResultException clientError)
        {
            var body = clientError.GetRawResponse()?.Content?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(body))
            {
                // A misbehaving or hostile model server can answer an error with a huge body; cap it before it is
                // copied into the transcript, the log, and — for a delegated session — the on-disk audit log and
                // the orchestrator's task result. A couple of KB is plenty to show the real reason.
                if (body.Length > MaxErrorBodyChars)
                {
                    body = string.Concat(body.AsSpan(0, MaxErrorBodyChars), "… (truncated)");
                }

                return $"{clientError.Message}\n{body}";
            }
        }

        return ex.Message;
    }

    private const int MaxErrorBodyChars = 2000;

    // AC-720: the HTTP status is the one structured signal an OpenAI-compatible server gives — the SDK
    // otherwise collapses everything into free text (_DescribeError above). Presentation-only classification;
    // an unrecognised status (including "no response received", Status == 0) stays Unknown/informational.
    internal static SessionErrorKind _ClassifyError(Exception ex) => ex switch
    {
        ClientResultException { Status: 401 or 403 } => SessionErrorKind.AuthRequired,
        ClientResultException { Status: 429 } => SessionErrorKind.RateLimited,
        ClientResultException { Status: >= 500 } => SessionErrorKind.ServiceUnavailable,
        _ => SessionErrorKind.Unknown,
    };

    // A 429's Retry-After header — RFC 9110 §10.2.3 allows either delta-seconds or an HTTP-date.
    internal static DateTimeOffset? _RetryAfterFrom(Exception ex)
    {
        if (ex is not ClientResultException clientError
            || clientError.GetRawResponse()?.Headers.TryGetValue("Retry-After", out var value) != true)
        {
            return null;
        }

        if (int.TryParse(value, out var seconds))
        {
            return DateTimeOffset.UtcNow.AddSeconds(seconds);
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var absolute)
            ? absolute
            : null;
    }

    public Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        _turnCancellation?.Cancel();
        return Task.CompletedTask;
    }

    public Task SetModelAsync(string? model, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            _model = model;
        }

        return Task.CompletedTask;
    }

    public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default)
    {
        _gate.Respond(toolUseId, allow);
        return Task.CompletedTask;
    }

    public Task AllowPermissionAlwaysAsync(string toolUseId, string toolName, string proposedInputJson, PermissionRuleScope scope, CancellationToken cancellationToken = default)
    {
        _gate.AllowAlways(toolUseId, toolName);
        return Task.CompletedTask;
    }

    public Task SetAutoApproveToolsAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        _gate.SetAutoApprove(enabled);
        return Task.CompletedTask;
    }

    public Task SetDelegatedToolGateAsync(string ceiling, IReadOnlyList<string> allowedTools, CancellationToken cancellationToken = default)
    {
        _gate.SetDelegatedGate(ceiling, allowedTools);
        return Task.CompletedTask;
    }

    // No live control channel on an HTTP provider — these Claude-CLI-only operations are deliberate no-ops.
    public Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetMaxThinkingTokensAsync(int maxThinkingTokens, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        _turnCancellation?.Cancel();
        _turnCancellation?.Dispose();
        _gate.CancelPending();

        if (_toolSession is not null)
        {
            await _toolSession.DisposeAsync().ConfigureAwait(false);
        }

        (_agent as IDisposable)?.Dispose();
    }

    private static string? _ModelFrom(ProviderConfig config) => config switch
    {
        OllamaConfig ollama => ollama.Model,
        LmStudioConfig lmStudio => lmStudio.Model,
        _ => null,
    };

    private static string? _SystemPromptFrom(ProviderConfig config) => config switch
    {
        OllamaConfig ollama => ollama.SystemPrompt,
        LmStudioConfig lmStudio => lmStudio.SystemPrompt,
        _ => null,
    };

    // The system prompt to seed: the profile's own base prompt with the host's hidden per-session prompt (AC-180)
    // appended after it, so an embedded run's brief lands on top of the profile without replacing it. Either being
    // absent falls back to the other; both absent seeds nothing.
    private static string? _CombineSystemPrompt(string? profilePrompt, IReadOnlyDictionary<string, string>? launchOptions)
    {
        var appendPrompt = launchOptions is not null
            && launchOptions.TryGetValue(WellKnownPluginSessionOptions.AppendSystemPrompt, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : null;

        if (appendPrompt is null)
        {
            return profilePrompt;
        }

        return string.IsNullOrWhiteSpace(profilePrompt) ? appendPrompt : $"{profilePrompt}\n\n{appendPrompt}";
    }
}
