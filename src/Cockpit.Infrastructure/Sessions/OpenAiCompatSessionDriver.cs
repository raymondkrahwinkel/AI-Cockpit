using System.ClientModel;
using System.Collections.Concurrent;
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

/// <summary>
/// <see cref="ISessionDriver"/> for the local OpenAI-compatible providers (Ollama, LM Studio) via
/// Microsoft.Extensions.AI's <see cref="IChatClient"/>. It streams assistant text, holds the conversation
/// history itself (HTTP is stateless), and — when the shared MCP registry has servers — runs an agentic
/// tool-loop (#26): the model's tool calls are gated through the cockpit's PermissionRequested flow and
/// executed via MCP only on approval. The Claude-CLI-specific control operations (permission mode, thinking
/// budget) remain no-ops; <see cref="Capabilities"/> tells the UI which controls to show.
/// </summary>
internal sealed class OpenAiCompatSessionDriver : ISessionDriver, IToolApprovalGate, ITransientService
{
    private readonly IChatClientFactory _chatClientFactory;
    private readonly IMcpToolProvider _mcpToolProvider;
    private readonly ILogger<OpenAiCompatSessionDriver> _logger;

    private readonly Channel<SessionEvent> _events = Channel.CreateUnbounded<SessionEvent>();
    private readonly List<ChatMessage> _history = [];
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingApprovals = new();
    private readonly ConcurrentDictionary<string, byte> _alwaysAllowedTools = new();
    private volatile bool _autoApproveTools;

    // Non-interactive delegated gate (AC-79): when a ceiling is set, this session has no human to prompt, so a
    // tool call is decided against the ceiling + allow-list rather than raising PermissionRequested. Null for an
    // ordinary interactive session.
    private volatile string? _delegatedGateCeiling;
    private volatile IReadOnlySet<string>? _delegatedGateAllowList;

    private IChatClient? _agent;
    private IMcpToolSession? _toolSession;
    private List<AITool> _gatedTools = [];
    private string? _model;
    private string? _sessionId;
    private CancellationTokenSource? _turnCancellation;

    // Set once a turn makes at least one tool call (AC-132), so a turn that ends with no assistant text and no
    // tool activity can be surfaced as "no response" rather than a silent success. Reset at each turn start;
    // turns are serialised (one _RunTurnAsync in flight at a time), and the tool loop can run the call on a
    // pool thread, so a volatile flag rather than a local.
    private volatile bool _turnHadToolActivity;

    public OpenAiCompatSessionDriver(IChatClientFactory chatClientFactory, IMcpToolProvider mcpToolProvider, ILogger<OpenAiCompatSessionDriver> logger)
    {
        _chatClientFactory = chatClientFactory;
        _mcpToolProvider = mcpToolProvider;
        _logger = logger;
    }

    // Tool support is set once the MCP servers connect (below); permission mode / model switch / thinking
    // stay off — the local model is fixed by its profile and tool approval is per-call, not a Claude mode.
    // SupportsVision stays false too: SendUserMessageAsync below ignores the images parameter entirely, so
    // advertising vision support here would be the exact dead promise #64 exists to prevent (fase 2 adds
    // real image-block support before this ever flips true).
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

    // launchOptions is unused: a built-in HTTP chat provider declares no per-session launch options.
    public async Task StartAsync(SessionProfile? profile = null, string? permissionMode = null, string? model = null, IReadOnlySet<string>? enabledMcpServerNames = null, string? workingDirectory = null, SessionResume? resume = null, IReadOnlyDictionary<string, string>? launchOptions = null, CancellationToken cancellationToken = default)
    {
        // workingDirectory is used only to confine file tools (below): a local session talks HTTP to a model server with
        // no cwd, but its file access rides MCP servers, so an isolated run confines those to this directory instead.
        var config = profile?.ProviderConfig
            ?? throw new InvalidOperationException($"{nameof(OpenAiCompatSessionDriver)} requires a profile with an OpenAI-compatible provider config.");

        Profile = profile;
        _model = string.IsNullOrWhiteSpace(model) ? _ModelFrom(config) : model;
        _sessionId = Guid.NewGuid().ToString();

        // Wrap the chat client in the agentic function-invocation loop; each MCP tool is gated so a tool
        // call is executed only after the operator approves it (the gate is this driver).
        _agent = new ChatClientBuilder(_chatClientFactory.Create(config)).UseFunctionInvocation().Build();
        // AC-89: pass this session's pane id (the App sets it as the cockpit.pane-id launch option) so the tool loop
        // connects to the cockpit endpoints on a per-session token — the consent broker then scopes on this pane, not
        // the id the local model declares.
        var paneId = launchOptions is not null && launchOptions.TryGetValue(WellKnownPluginSessionOptions.PaneId, out var value) ? value : null;

        // Confinement (AC-174, Raymond 2026-07-22): the host sets this flag when it isolates the session in a worktree.
        // A local model reaches files only through MCP servers, so honour it by asking the tool provider to confine file
        // tools to the working directory — re-root the filesystem server there and drop every escape channel. Only a
        // session that actually gets confined here may then vouch it (the capability below), so the flag alone is never
        // enough — it needs a real working directory to confine to.
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
        _toolSession = await _mcpToolProvider.ConnectAsync(selection, paneId, confineRoot, cancellationToken).ConfigureAwait(false);

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

        _gatedTools = _toolSession.Tools.Select(tool => (AITool)new GatedTool(tool, this)).ToList();
        // SupportsTools flips true once servers connected. ConfinesFileAccessToWorkingDirectory is vouched only when we
        // actually confined this session (confineRoot set → the tool provider re-rooted file access to the worktree and
        // dropped every escape channel); the host's fail-closed isolation gate reads it, so it must never read true on a
        // session that was not confined.
        Capabilities = Capabilities with
        {
            SupportsTools = _gatedTools.Count > 0,
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

        // Report the actual tool names (not the server names) so the session's "N tools" count is real and
        // the UI can show exactly which tools — e.g. read_file — the local model has, making it verifiable
        // whether a file tool is even available before wondering why the model didn't call one.
        _events.Writer.TryWrite(new SessionInitialized { SessionId = _sessionId, Cwd = string.Empty, Tools = [.. _gatedTools.Select(tool => tool.Name)] });
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
        var toolsAvailable = _gatedTools.Count > 0;
        _turnHadToolActivity = false;

        try
        {
            await _StreamTurnAsync(toolsAvailable ? _gatedTools : null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A genuine user interrupt — the token this turn runs under was cancelled. Keep the clean
            // "interrupted" turn with no error row. An OperationCanceledException the SDK throws while ABORTING
            // the stream on a non-2xx response leaves the token uncancelled, so it fails this filter and falls
            // through to an error branch rather than masquerading as an interrupt (AC-132).
            _events.Writer.TryWrite(new TurnCompleted { SessionId = _sessionId, Subtype = "interrupted", Result = null, IsError = false, StopReason = "interrupt" });
        }
        catch (Exception ex)
        {
            var message = _DescribeError(ex);

            // AC-135: the model rejected the request because it cannot do tool-calling in this runtime — a GGUF
            // chat template whose tool-parser generation fails the moment `tools` are sent (seen with mistral-nemo
            // in LM Studio). Rather than fail the turn, note it and retry once with no tools, so a plain question
            // still gets an answer. The note is streamed as assistant text, NOT a SessionError: a SessionError
            // reads to the UI as the turn ending (it clears the busy state), which would let a second turn start
            // against this turn's history while the retry is still streaming.
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
            _events.Writer.TryWrite(new SessionError { SessionId = _sessionId, Message = message, Exception = ex });
            _events.Writer.TryWrite(new TurnCompleted { SessionId = _sessionId, Subtype = "error", Result = null, IsError = true });
        }
    }

    /// <summary>
    /// Streams one model turn under <paramref name="tools"/> (null = no tools), emitting the assistant deltas and a
    /// terminal <see cref="TurnCompleted"/>. A turn that produces no visible text and no tool call surfaces a
    /// "no response" notice rather than a silent success, and only a turn with text is carried into the history so a
    /// blank assistant message never rides along in later requests (AC-132). Exceptions propagate to the caller,
    /// which decides between an interrupt, a tool-unsupported retry, and a plain error.
    /// </summary>
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
        _events.Writer.TryWrite(new SessionError { SessionId = _sessionId, Message = message, Exception = ex });
        _events.Writer.TryWrite(new TurnCompleted { SessionId = _sessionId, Subtype = "error", Result = null, IsError = true });
    }

    /// <summary>
    /// Whether a failed request looks like the local runtime refusing tool-calling itself — a GGUF/chat-template
    /// tool-parser that cannot be generated, or a server saying tools are unsupported — as opposed to an ordinary
    /// request error. A heuristic over the response body (AC-135): the message wording is the only signal a local
    /// OpenAI-compatible server gives, and only weighed when tools were actually sent this turn.
    /// </summary>
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

    /// <summary>
    /// The most useful message for a failed turn: the exception message, plus — for a
    /// <see cref="ClientResultException"/> from the OpenAI SDK — the HTTP response body. A local server puts the
    /// real reason there (an <c>exceed_context_size_error</c>, a tool-template parser failure, …), which the
    /// exception's own "HTTP 400 (Bad Request)" message hides (AC-132).
    /// </summary>
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

    async Task<ToolApprovalResult> IToolApprovalGate.RequestApprovalAsync(string toolUseId, string toolName, string inputJson, CancellationToken cancellationToken)
    {
        // A tool call means this turn produced something visible even if it ends with no assistant text, so the
        // no-response vangnet in _RunTurnAsync must not fire for it (AC-132).
        _turnHadToolActivity = true;

        // Surface the call in the transcript, then either auto-allow (an always-allow rule this session), decide
        // it non-interactively (a delegated session), or prompt and await the operator's decision.
        _events.Writer.TryWrite(new ToolUseRequested { SessionId = _sessionId, ToolUseId = toolUseId, ToolName = toolName, InputJson = inputJson });

        // Auto-approve mode (the session's "allow all tools" toggle) or a per-tool always-allow rule runs the
        // call without prompting — the tool row is still emitted above, so it stays visible either way.
        if (_autoApproveTools || _alwaysAllowedTools.ContainsKey(toolName))
        {
            return ToolApprovalResult.Allow;
        }

        // A delegated session has no human to answer a prompt (AC-79): decide non-interactively against the
        // profile's permission ceiling and tool allow-list instead of raising PermissionRequested. A denial
        // carries its reason to the model (via GatedTool) and never blocks — no PermissionRequested is emitted.
        if (_delegatedGateCeiling is { } ceiling)
        {
            var toolClass = _toolSession?.ToolClasses.GetValueOrDefault(toolName, ToolPermissionClass.Unknown) ?? ToolPermissionClass.Unknown;
            var onAllowList = _delegatedGateAllowList?.Contains(toolName) == true;
            var decision = DelegatedToolPermissionPolicy.Decide(ceiling, toolClass, toolName, onAllowList);
            return decision.IsAllowed ? ToolApprovalResult.Allow : ToolApprovalResult.Deny(decision.DenyMessage);
        }

        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingApprovals[toolUseId] = pending;
        _events.Writer.TryWrite(new PermissionRequested { SessionId = _sessionId, ToolUseId = toolUseId, ToolName = toolName, InputJson = inputJson });

        using (cancellationToken.Register(() => pending.TrySetResult(false)))
        {
            var approved = await pending.Task.ConfigureAwait(false);
            return approved ? ToolApprovalResult.Allow : ToolApprovalResult.Deny(null);
        }
    }

    void IToolApprovalGate.ReportToolResult(string toolUseId, string content, bool isError)
    {
        _pendingApprovals.TryRemove(toolUseId, out _);
        _events.Writer.TryWrite(new ToolResult { SessionId = _sessionId, ToolUseId = toolUseId, Content = content, IsError = isError });
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
        if (_pendingApprovals.TryRemove(toolUseId, out var decision))
        {
            decision.TrySetResult(allow);
        }

        return Task.CompletedTask;
    }

    public Task AllowPermissionAlwaysAsync(string toolUseId, string toolName, string proposedInputJson, PermissionRuleScope scope, CancellationToken cancellationToken = default)
    {
        _alwaysAllowedTools.TryAdd(toolName, 0);
        if (_pendingApprovals.TryRemove(toolUseId, out var decision))
        {
            decision.TrySetResult(true);
        }

        return Task.CompletedTask;
    }

    public Task SetAutoApproveToolsAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        _autoApproveTools = enabled;

        // Flipping it on frees any prompt already waiting, so the operator does not have to answer a prompt
        // they just chose to stop seeing.
        if (enabled)
        {
            foreach (var pending in _pendingApprovals.Values)
            {
                pending.TrySetResult(true);
            }
        }

        return Task.CompletedTask;
    }

    public Task SetDelegatedToolGateAsync(string ceiling, IReadOnlyList<string> allowedTools, CancellationToken cancellationToken = default)
    {
        // Set the allow-list first, then the ceiling — the ceiling being non-null is what arms the gate in
        // RequestApprovalAsync, so the list it reads is already in place by the time a decision consults it.
        // Coerce a null ceiling to empty (not null): a caller that asked for the gate must always get it armed —
        // an empty ceiling grades as the most restrictive (read-only only), never "unarmed" (which would fall
        // through to a prompt that hangs a headless session).
        _delegatedGateAllowList = new HashSet<string>(allowedTools, StringComparer.Ordinal);
        _delegatedGateCeiling = ceiling ?? string.Empty;
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
        foreach (var pending in _pendingApprovals.Values)
        {
            pending.TrySetResult(false);
        }

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
