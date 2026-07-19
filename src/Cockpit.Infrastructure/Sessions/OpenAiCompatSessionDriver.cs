using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
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
        // workingDirectory is unused: a local OpenAI-compatible session talks HTTP to a model server; there is
        // no spawned process with a cwd to point at a project folder.
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
        _toolSession = await _mcpToolProvider.ConnectAsync(enabledMcpServerNames, paneId, cancellationToken).ConfigureAwait(false);
        _gatedTools = _toolSession.Tools.Select(tool => (AITool)new GatedTool(tool, this)).ToList();
        Capabilities = Capabilities with { SupportsTools = _gatedTools.Count > 0 };

        // Seed the conversation with the profile's base system prompt so every turn carries it (HTTP is
        // stateless — the client owns the history, so a system message once at the front is enough).
        var systemPrompt = _SystemPromptFrom(config);
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
        var options = new ChatOptions { ModelId = _model, Tools = _gatedTools.Count > 0 ? _gatedTools : null };
        var assistant = new StringBuilder();

        try
        {
            await foreach (var update in _agent!.GetStreamingResponseAsync(_history, options, cancellationToken).ConfigureAwait(false))
            {
                var delta = update.Text;
                if (!string.IsNullOrEmpty(delta))
                {
                    assistant.Append(delta);
                    _events.Writer.TryWrite(new AssistantTextDelta { SessionId = _sessionId, BlockIndex = 0, Text = delta });
                }
            }

            _history.Add(new ChatMessage(ChatRole.Assistant, assistant.ToString()));
            _events.Writer.TryWrite(new TurnCompleted { SessionId = _sessionId, Subtype = "success", Result = assistant.ToString(), IsError = false });
        }
        catch (OperationCanceledException)
        {
            _events.Writer.TryWrite(new TurnCompleted { SessionId = _sessionId, Subtype = "interrupted", Result = assistant.ToString(), IsError = false, StopReason = "interrupt" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenAI-compatible chat request failed");
            _events.Writer.TryWrite(new SessionError { SessionId = _sessionId, Message = ex.Message, Exception = ex });
            _events.Writer.TryWrite(new TurnCompleted { SessionId = _sessionId, Subtype = "error", Result = null, IsError = true });
        }
    }

    async Task<ToolApprovalResult> IToolApprovalGate.RequestApprovalAsync(string toolUseId, string toolName, string inputJson, CancellationToken cancellationToken)
    {
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
}
