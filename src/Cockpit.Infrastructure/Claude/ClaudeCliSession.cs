using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Claude;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Claude;

/// <summary>
/// <see cref="IClaudeSession"/> backed by a headless <c>claude</c> CLI process
/// (see <see cref="ClaudeCliProcess"/> for the exact invocation and doc grounding).
/// </summary>
/// <remarks>
/// Permission handling (F-C1 scope): the CLI's own interactive permission prompt only
/// exists in the *terminal* UI; a headless stream-json process has no stdin channel for
/// answering the CLI's own y/n prompt, and this driver does not yet wire a
/// <c>--permission-prompt-tool</c> MCP server or a PreToolUse hook. Every
/// <see cref="ToolUseRequested"/> is therefore additionally surfaced as a
/// <see cref="PermissionRequested"/> event so the UI can show an allow/deny affordance,
/// but <see cref="RespondToPermissionAsync"/> currently only records the decision — it does
/// not feed back into the running CLI process (there is no channel to do so with
/// <c>--permission-mode default</c> in headless mode without the extra MCP wiring). The
/// documented next increment is a <c>--permission-prompt-tool</c> MCP server that this host
/// implements and passes on the CLI command line, so <c>canUseTool</c>-equivalent decisions
/// flow back in-band. Until then, run with <c>--permission-mode acceptEdits</c> or
/// <c>bypassPermissions</c> if you need the CLI itself to proceed unattended.
/// </remarks>
internal sealed class ClaudeCliSession : IClaudeSession, ITransientService
{
    private readonly IClaudeCliProcess _process;
    private readonly ILogger<ClaudeCliSession> _logger;
    private readonly Channel<ClaudeSessionEvent> _events = Channel.CreateUnbounded<ClaudeSessionEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private Task? _pumpTask;
    private CancellationTokenSource? _pumpCancellation;
    private string? _sessionId;

    public ClaudeCliSession(IClaudeCliProcess process, ILogger<ClaudeCliSession> logger)
    {
        _process = process;
        _logger = logger;
    }

    public string? SessionId => _sessionId;

    public ClaudeProfile? Profile { get; private set; }

    public IAsyncEnumerable<ClaudeSessionEvent> Events => _events.Reader.ReadAllAsync();

    public Task StartAsync(ClaudeProfile? profile = null, string? permissionMode = null, string? model = null, CancellationToken cancellationToken = default)
    {
        Profile = profile;
        _process.Start(profile, permissionMode, model);
        _pumpCancellation = new CancellationTokenSource();
        _pumpTask = PumpOutputAsync(_pumpCancellation.Token);
        return Task.CompletedTask;
    }

    public async Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default)
    {
        // Wire shape per https://code.claude.com/docs/en/agent-sdk/streaming-vs-single-mode.md:
        // {"type":"user","message":{"role":"user","content":"..."}}
        // One user-message object per stdin line keeps the same persistent session/turn loop alive.
        var payload = new
        {
            type = "user",
            message = new
            {
                role = "user",
                content = text,
            },
        };

        var line = JsonSerializer.Serialize(payload);
        await _process.WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
    }

    public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default)
    {
        // See class remarks: no in-band feedback channel to the CLI process yet in F-C1.
        _logger.LogInformation(
            "Permission decision recorded locally (not yet wired to CLI): tool_use_id={ToolUseId} allow={Allow}",
            toolUseId,
            allow);
        return Task.CompletedTask;
    }

    public Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default) =>
        _SendControlRequestAsync(new { subtype = "set_permission_mode", mode }, cancellationToken);

    public Task SetModelAsync(string? model, CancellationToken cancellationToken = default) =>
        _SendControlRequestAsync(new { subtype = "set_model", model }, cancellationToken);

    public Task InterruptAsync(CancellationToken cancellationToken = default) =>
        _SendControlRequestAsync(new { subtype = "interrupt" }, cancellationToken);

    /// <summary>
    /// Builds and writes a single <c>control_request</c> line:
    /// <c>{"type":"control_request","request_id":"...","request":{"subtype":"...", ...}}</c>.
    /// UNVERIFIED wire shape — see <see cref="IClaudeSession.SetPermissionModeAsync"/> remarks.
    /// The matching <c>control_response</c> (if any) is not correlated back to the caller here;
    /// it will show up as an <see cref="UnknownEvent"/> in <see cref="Events"/> and is logged,
    /// not awaited/blocked on, per the F-C1 driver scope.
    /// </summary>
    private async Task _SendControlRequestAsync(object request, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString();
        var payload = new
        {
            type = "control_request",
            request_id = requestId,
            request,
        };

        var line = JsonSerializer.Serialize(payload);
        _logger.LogInformation("Sending control_request {RequestId}: {Line}", requestId, line);
        await _process.WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpOutputAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var line in _process.ReadLinesAsync(cancellationToken).ConfigureAwait(false))
            {
                await HandleLineAsync(line).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on disposal.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Claude CLI output pump failed");
            _events.Writer.TryWrite(new SessionError { SessionId = _sessionId, Message = "Output pump failed", Exception = ex });
        }
        finally
        {
            _events.Writer.TryComplete();
        }
    }

    private Task HandleLineAsync(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse stream-json line: {Line}", line);
            _events.Writer.TryWrite(new SessionError { SessionId = _sessionId, Message = $"Malformed stream-json line: {ex.Message}", Exception = ex });
            return Task.CompletedTask;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.TryGetProperty("session_id", out var sidProp) && sidProp.ValueKind == JsonValueKind.String)
            {
                _sessionId = sidProp.GetString();
            }

            // Control-protocol replies to our own SetPermissionModeAsync/SetModelAsync/InterruptAsync
            // control_requests (see _SendControlRequestAsync). Logged only, per F-C1 scope: nothing
            // here currently correlates a response back to its request_id or blocks on it. UNVERIFIED
            // wire shape — see IClaudeSession.SetPermissionModeAsync remarks.
            if (root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
                && typeProp.GetString() is "control_response" or "control_cancel_request")
            {
                _logger.LogInformation("Received {Type} line: {Line}", typeProp.GetString(), line);
                return Task.CompletedTask;
            }

            // A single line can carry multiple events (e.g. an assistant snapshot with several
            // content blocks); every tool_use additionally gets a derived PermissionRequested
            // since the CLI has no in-band feedback channel yet (see class remarks).
            foreach (var evt in ClaudeStreamJsonParser.ParseLine(line))
            {
                _events.Writer.TryWrite(evt);
                if (evt is ToolUseRequested toolUse)
                {
                    _events.Writer.TryWrite(new PermissionRequested
                    {
                        SessionId = _sessionId,
                        ToolUseId = toolUse.ToolUseId,
                        ToolName = toolUse.ToolName,
                        InputJson = toolUse.InputJson,
                    });
                }
            }
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _pumpCancellation?.Cancel();
        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        await _process.DisposeAsync().ConfigureAwait(false);
        _pumpCancellation?.Dispose();
    }
}
