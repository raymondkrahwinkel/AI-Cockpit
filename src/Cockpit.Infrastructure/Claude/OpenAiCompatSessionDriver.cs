using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Claude;
using Cockpit.Core.Claude.Permissions;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Claude;

/// <summary>
/// <see cref="ISessionDriver"/> for the local OpenAI-compatible providers (Ollama, LM Studio) via
/// Microsoft.Extensions.AI's <see cref="IChatClient"/>. Chat-only for now (#26 F2.1): it streams
/// assistant text and reports turn completion, holding the conversation history itself (HTTP is
/// stateless). The Claude-CLI-specific control operations are no-ops here; <see cref="Capabilities"/>
/// tells the UI not to offer them. The tool-loop (MCP function-calling + permission gating) lands in a
/// later increment, at which point tools/permissions flip on.
/// </summary>
// A classic constructor rather than a primary one: the driver owns real per-session state (the event
// channel and history) that reads more clearly initialized in a body than captured as parameters.
internal sealed class OpenAiCompatSessionDriver : ISessionDriver
{
    private readonly IChatClientFactory _chatClientFactory;
    private readonly ILogger<OpenAiCompatSessionDriver> _logger;

    private readonly Channel<ClaudeSessionEvent> _events = Channel.CreateUnbounded<ClaudeSessionEvent>();
    private readonly List<ChatMessage> _history = [];

    private IChatClient? _chatClient;
    private string? _model;
    private string? _sessionId;
    private CancellationTokenSource? _turnCancellation;

    public OpenAiCompatSessionDriver(IChatClientFactory chatClientFactory, ILogger<OpenAiCompatSessionDriver> logger)
    {
        _chatClientFactory = chatClientFactory;
        _logger = logger;
    }

    // Chat-only: no tools/permissions/plan yet; the model can be changed between turns (a new request),
    // no live control channel; thinking off until a reasoning-capable path is added.
    public SessionCapabilities Capabilities { get; } = new(
        SupportsTools: false,
        SupportsPermissions: false,
        SupportsLiveModelSwitch: true,
        SupportsPlanMode: false,
        SupportsThinking: false);

    public string? SessionId => _sessionId;

    public ClaudeProfile? Profile { get; private set; }

    public IAsyncEnumerable<ClaudeSessionEvent> Events => _events.Reader.ReadAllAsync();

    public Task StartAsync(ClaudeProfile? profile = null, string? permissionMode = null, string? model = null, CancellationToken cancellationToken = default)
    {
        var config = profile?.ProviderConfig
            ?? throw new InvalidOperationException($"{nameof(OpenAiCompatSessionDriver)} requires a profile with an OpenAI-compatible provider config.");

        Profile = profile;
        _chatClient = _chatClientFactory.Create(config);
        _model = string.IsNullOrWhiteSpace(model) ? _ModelFrom(config) : model;
        _sessionId = Guid.NewGuid().ToString();

        _events.Writer.TryWrite(new SessionInitialized { SessionId = _sessionId, Cwd = string.Empty, Tools = [] });
        return Task.CompletedTask;
    }

    public Task SendUserMessageAsync(string text, IReadOnlyList<ImageAttachment>? images = null, CancellationToken cancellationToken = default)
    {
        if (_chatClient is null)
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
        var options = new ChatOptions { ModelId = _model };
        var assistant = new StringBuilder();

        try
        {
            await foreach (var update in _chatClient!.GetStreamingResponseAsync(_history, options, cancellationToken).ConfigureAwait(false))
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

    // No live control channel on an HTTP provider — these are the Claude-CLI-only operations the UI hides
    // via Capabilities, so they are deliberate no-ops rather than throwing.
    public Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetMaxThinkingTokensAsync(int maxThinkingTokens, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task AllowPermissionAlwaysAsync(string toolUseId, string toolName, string proposedInputJson, PermissionRuleScope scope, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        _turnCancellation?.Cancel();
        _turnCancellation?.Dispose();
        _chatClient?.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string? _ModelFrom(ProviderConfig config) => config switch
    {
        OllamaConfig ollama => ollama.Model,
        LmStudioConfig lmStudio => lmStudio.Model,
        _ => null,
    };
}
