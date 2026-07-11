using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GeminiProvider;

/// <summary>
/// <see cref="IPluginSessionDriver"/> for this plugin's Gemini/OpenAI providers, over an OpenAI-compatible
/// <see cref="IChatClient"/> (#45) — mirrors the shape of the host's own
/// <c>Cockpit.Infrastructure.Claude.OpenAiCompatSessionDriver</c> (history/streaming/error handling), minus
/// its MCP tool-loop: a plugin has no tool source of its own in fase A, so this driver is chat/streaming
/// only (<see cref="Capabilities"/> reports no tool support).
/// </summary>
internal sealed class OpenAiCompatPluginSessionDriver(IChatClient chatClient, string defaultModel) : IPluginSessionDriver
{
    private readonly Channel<PluginSessionEvent> _events = Channel.CreateUnbounded<PluginSessionEvent>();
    private readonly List<ChatMessage> _history = [];

    private string? _sessionId;
    private string _model = defaultModel;
    private CancellationTokenSource? _turnCancellation;

    public PluginSessionCapabilities Capabilities { get; } = new(SupportsTools: false, SupportsPermissions: false);

    public string? SessionId => _sessionId;

    public IAsyncEnumerable<PluginSessionEvent> Events => _events.Reader.ReadAllAsync();

    public Task StartAsync(string? model = null, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            _model = model;
        }

        _sessionId = Guid.NewGuid().ToString();
        _events.Writer.TryWrite(new PluginSessionInitialized { SessionId = _sessionId, Tools = [] });
        return Task.CompletedTask;
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
                    _events.Writer.TryWrite(new PluginAssistantTextDelta { SessionId = _sessionId, BlockIndex = 0, Text = delta });
                }
            }

            _history.Add(new ChatMessage(ChatRole.Assistant, assistant.ToString()));
            _events.Writer.TryWrite(new PluginTurnCompleted { SessionId = _sessionId, Subtype = "success", Result = assistant.ToString(), IsError = false });
        }
        catch (OperationCanceledException)
        {
            _events.Writer.TryWrite(new PluginTurnCompleted { SessionId = _sessionId, Subtype = "interrupted", Result = assistant.ToString(), IsError = false, StopReason = "interrupt" });
        }
        catch (Exception ex)
        {
            _events.Writer.TryWrite(new PluginSessionError { SessionId = _sessionId, Message = ex.Message });
            _events.Writer.TryWrite(new PluginTurnCompleted { SessionId = _sessionId, Subtype = "error", Result = null, IsError = true });
        }
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
        _events.Writer.TryComplete();
        _turnCancellation?.Cancel();
        _turnCancellation?.Dispose();
        (chatClient as IDisposable)?.Dispose();
        await Task.CompletedTask;
    }
}
