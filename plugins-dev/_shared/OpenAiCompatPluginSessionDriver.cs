using System.ClientModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugins.OpenAiCompat;

// The OpenAI-compatible chat driver every provider plugin of this family runs (AC-964): history, streaming,
// error classification, and an agentic tool loop over the toolset the host mounts and gates. Linked into each
// plugin as source (<Compile Include>) rather than shipped as a shared assembly, so every plugin still builds,
// versions and installs as one self-contained dll — while the loop itself exists once.
internal sealed class OpenAiCompatPluginSessionDriver(IChatClient chatClient, string defaultModel) : IPluginSessionDriver
{
    private readonly PluginSessionEventPublisher _events = new();
    private readonly List<ChatMessage> _history = [];

    // The agentic wrapper around the model client: it runs the tool calls the model asks for and feeds the
    // results back, so a turn can span several round trips. Built at start, once, whether or not there are tools.
    private IChatClient? _agent;
    private IReadOnlyList<AITool> _tools = [];

    // Every tool name this session can reach, which is more than _tools whenever the host kept part of the
    // catalogue behind a search proxy. What the init event reports, so the header's count is the real one.
    private IReadOnlyList<string> _reachableToolNames = [];

    private string? _sessionId;
    private string _model = defaultModel;
    private CancellationTokenSource? _turnCancellation;

    // Tool support is not a property of the provider but of the session: it is true once the host actually
    // mounted tools for it, and stays false for a session started with no MCP servers.
    public PluginSessionCapabilities Capabilities { get; private set; } = new(SupportsTools: false, SupportsPermissions: false);

    public string? SessionId => _sessionId;

    // AC-408: this driver keeps its own in-memory history, not a server-side conversation, so a resume id it
    // is handed is not actually resumable — Unsupported says that honestly instead of implying otherwise.
    public PluginConversationId Conversation => PluginConversationId.Unsupported;

    public IAsyncEnumerable<PluginSessionEvent> Events => _events.Events;

    public Task StartAsync(string? model = null, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            _model = model;
        }

        _sessionId = Guid.NewGuid().ToString();
        _agent = new ChatClientBuilder(chatClient).UseFunctionInvocation().Build();
        _events.Publish(new PluginSessionInitialized { SessionId = _sessionId, Tools = _reachableToolNames });
        return Task.CompletedTask;
    }

    // AC-180: the hidden per-session system prompt, seeded once at the front of this driver's own history.
    public Task StartAsync(string? model, string? workingDirectory, string? resumeSessionId, IReadOnlyDictionary<string, string>? options, IReadOnlyList<PluginMcpServer>? mcpServers, CancellationToken cancellationToken)
    {
        if (options is not null
            && options.TryGetValue(WellKnownPluginSessionOptions.AppendSystemPrompt, out var appendSystemPrompt)
            && !string.IsNullOrWhiteSpace(appendSystemPrompt))
        {
            _history.Add(new ChatMessage(ChatRole.System, appendSystemPrompt.Trim()));
        }

        return StartAsync(model, cancellationToken);
    }

    // AC-964: the host mounted this session's MCP servers, gated every tool and will write the transcript's tool
    // rows; all this driver adds is offering them to the model and calling back. The names it reports are the
    // reachable ones, so a session that keeps most of its catalogue behind a search proxy still counts honestly.
    public Task StartAsync(string? model, string? workingDirectory, string? resumeSessionId, IReadOnlyDictionary<string, string>? options, IReadOnlyList<PluginMcpServer>? mcpServers, IReadOnlyDictionary<string, string>? environment, IPluginToolset? toolset, CancellationToken cancellationToken)
    {
        if (toolset is { Tools.Count: > 0 })
        {
            _tools = [.. toolset.Tools.Select(descriptor => new HostTool(descriptor, toolset))];
            _reachableToolNames = toolset.ReachableToolNames;

            // SupportsPermissions stays false on purpose: it means Claude's permission *modes*, and a session
            // that reports tools without them is what gives the header its per-call "Allow all tools" toggle —
            // the gating this route actually has.
            Capabilities = Capabilities with { SupportsTools = true };
        }

        return StartAsync(model, workingDirectory, resumeSessionId, options, mcpServers, cancellationToken);
    }

    public Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default)
    {
        // Run the turn in the background so the caller returns immediately and consumes the reply through
        // Events, mirroring the host's own OpenAiCompatSessionDriver.
        _turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = _RunTurnAsync(text, _turnCancellation.Token);
        return Task.CompletedTask;
    }

    private async Task _RunTurnAsync(string text, CancellationToken cancellationToken)
    {
        _history.Add(new ChatMessage(ChatRole.User, text));
        var options = new ChatOptions { ModelId = _model, Tools = _tools.Count > 0 ? [.. _tools] : null };
        var assistant = new StringBuilder();

        try
        {
            await foreach (var update in (_agent ?? chatClient).GetStreamingResponseAsync(_history, options, cancellationToken).ConfigureAwait(false))
            {
                var delta = update.Text;
                if (!string.IsNullOrEmpty(delta))
                {
                    assistant.Append(delta);
                    _events.Publish(new PluginAssistantTextDelta { SessionId = _sessionId, BlockIndex = 0, Text = delta });
                }
            }

            _history.Add(new ChatMessage(ChatRole.Assistant, assistant.ToString()));
            _events.Publish(new PluginTurnCompleted { SessionId = _sessionId, Subtype = "success", Result = assistant.ToString(), IsError = false });
        }
        catch (OperationCanceledException)
        {
            _events.Publish(new PluginTurnCompleted { SessionId = _sessionId, Subtype = "interrupted", Result = assistant.ToString(), IsError = false, StopReason = "interrupt" });
        }
        catch (Exception ex)
        {
            _events.Publish(new PluginSessionError
            {
                SessionId = _sessionId,
                Message = ex.Message,
                Kind = _ClassifyError(ex),
                RetryAfter = _RetryAfterFrom(ex),
            });
            _events.Publish(new PluginTurnCompleted { SessionId = _sessionId, Subtype = "error", Result = null, IsError = true });
        }
    }

    // AC-720: the HTTP status is the one structured signal an OpenAI-compatible server gives — mirrors
    // the host's own Cockpit.Infrastructure.Sessions.OpenAiCompatSessionDriver._ClassifyError. An
    // unrecognised status stays Unknown/informational rather than a guessed severity.
    private static PluginSessionErrorKind _ClassifyError(Exception ex) => ex switch
    {
        ClientResultException { Status: 401 or 403 } => PluginSessionErrorKind.AuthRequired,
        ClientResultException { Status: 429 } => PluginSessionErrorKind.RateLimited,
        ClientResultException { Status: >= 500 } => PluginSessionErrorKind.ServiceUnavailable,
        _ => PluginSessionErrorKind.Unknown,
    };

    // A 429's Retry-After header — RFC 9110 §10.2.3 allows either delta-seconds or an HTTP-date.
    private static DateTimeOffset? _RetryAfterFrom(Exception ex)
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

    // Permission prompts for this session's tools are raised and answered host-side, so nothing ever reaches
    // this driver to resolve — kept as a real (rather than throwing) no-op so a speculative call stays safe.
    public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _events.TryComplete();
        _turnCancellation?.Cancel();
        _turnCancellation?.Dispose();
        (_agent as IDisposable)?.Dispose();
        (chatClient as IDisposable)?.Dispose();
        await Task.CompletedTask;
    }

    // One host-mounted tool as the model client understands it. It carries the host's schema verbatim and does
    // nothing but forward the call, because everything that decides whether the call may run lives host-side.
    private sealed class HostTool(PluginToolDescriptor descriptor, IPluginToolset toolset) : AIFunction
    {
        private static readonly JsonElement EmptySchema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

        public override string Name => descriptor.Name;

        public override string Description => descriptor.Description ?? string.Empty;

        public override JsonElement JsonSchema { get; } = _ParseSchema(descriptor.InputSchemaJson);

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            await toolset.InvokeAsync(descriptor.Name, _SerializeArguments(arguments), cancellationToken).ConfigureAwait(false);

        // A schema that will not parse would otherwise take the whole session down at start; an empty object
        // schema keeps the tool callable with no declared parameters, which the host validates anyway.
        private static JsonElement _ParseSchema(string inputSchemaJson)
        {
            try
            {
                return JsonDocument.Parse(inputSchemaJson).RootElement.Clone();
            }
            catch (JsonException)
            {
                return EmptySchema;
            }
        }

        private static string _SerializeArguments(AIFunctionArguments arguments)
        {
            try
            {
                return JsonSerializer.Serialize(arguments.ToDictionary(pair => pair.Key, pair => pair.Value));
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                return "{}";
            }
        }
    }
}
