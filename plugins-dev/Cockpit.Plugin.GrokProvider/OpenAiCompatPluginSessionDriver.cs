using System.ClientModel;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GrokProvider;

// `IPluginSessionDriver` for this plugin's Grok provider, over an OpenAI-compatible
// `IChatClient` (#45/#63/AC-724) — mirrors the shape of the host's own
// `Cockpit.Infrastructure.Sessions.OpenAiCompatSessionDriver` (history/streaming/error handling), minus
// its MCP tool-loop: a plugin has no tool source of its own, so this driver is chat/streaming only
// (`Capabilities` reports no tool support).
internal sealed class OpenAiCompatPluginSessionDriver(IChatClient chatClient, string defaultModel) : IPluginSessionDriver
{
    private readonly PluginSessionEventPublisher _events = new();
    private readonly List<ChatMessage> _history = [];

    private string? _sessionId;
    private string _model = defaultModel;
    private CancellationTokenSource? _turnCancellation;

    public PluginSessionCapabilities Capabilities { get; } = new(SupportsTools: false, SupportsPermissions: false);

    public string? SessionId => _sessionId;

    // This driver accepts resumeSessionId (the StartAsync overload below) but ignores it — it keeps its own
    // in-memory history rather than a server-side conversation (see the comment there), so the id in SessionId
    // is not actually resumable. Unsupported says that honestly instead of the default Known(SessionId) implying
    // a resume that would silently start a fresh chat with no history (AC-408).
    public PluginConversationId Conversation => PluginConversationId.Unsupported;

    public IAsyncEnumerable<PluginSessionEvent> Events => _events.Events;

    public Task StartAsync(string? model = null, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            _model = model;
        }

        _sessionId = Guid.NewGuid().ToString();
        _events.Publish(new PluginSessionInitialized { SessionId = _sessionId, Tools = [] });
        return Task.CompletedTask;
    }

    // The host's full start surface. All this driver needs from it is the hidden per-session system prompt the host
    // folds into the options map (AC-180 — an embedded run's brief): seeded once at the front of the history so every
    // turn carries it, since this HTTP driver owns its own history. Everything else (working dir, resume, MCP) has no
    // meaning for a plain chat provider, so it drops through to the base overload.
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
        var options = new ChatOptions { ModelId = _model };
        var assistant = new StringBuilder();

        try
        {
            await foreach (var update in chatClient.GetStreamingResponseAsync(_history, options, cancellationToken).ConfigureAwait(false))
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

    // No tool source in this driver yet, so nothing ever raises a PluginPermissionRequested to respond to —
    // kept as a real (rather than throwing) no-op so a host that calls it speculatively stays safe.
    public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _events.TryComplete();
        _turnCancellation?.Cancel();
        _turnCancellation?.Dispose();
        (chatClient as IDisposable)?.Dispose();
        await Task.CompletedTask;
    }
}
