using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Claude;
using Cockpit.Core.Claude.Permissions;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Claude;

/// <summary>
/// <see cref="IClaudeSession"/> backed by a headless <c>claude</c> CLI process
/// (see <see cref="ClaudeCliProcess"/> for the exact invocation and doc grounding).
/// </summary>
/// <remarks>
/// Permission handling: the CLI is spawned with <c>--permission-prompt-tool</c> pointing at the
/// cockpit's shared in-process MCP server (see <c>PermissionMcpServer</c>). For any tool that
/// genuinely needs approval, the CLI calls that tool over HTTP; the request carries no
/// <c>session_id</c>, so it is correlated on <c>tool_use_id</c>. This session sees the
/// <c>tool_use</c> (and its id) in its own stream first, registers the id as its own, and surfaces
/// a <see cref="PermissionRequested"/> event for the UI. When the operator answers,
/// <see cref="RespondToPermissionAsync"/> resolves the pending decision through the
/// <see cref="IPermissionCoordinator"/>, which unblocks the MCP tool and feeds the allow/deny back
/// in-band. Any still-pending decisions are denied on dispose so a closing session never leaves
/// the CLI blocked.
/// </remarks>
internal sealed class ClaudeCliSession : IClaudeSession, ITransientService
{
    private readonly IClaudeCliProcess _process;
    private readonly IPermissionCoordinator _permissionCoordinator;
    private readonly IPermissionRuleStore _permissionRuleStore;
    private readonly ILogger<ClaudeCliSession> _logger;
    private readonly Channel<ClaudeSessionEvent> _events = Channel.CreateUnbounded<ClaudeSessionEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly ConcurrentDictionary<string, byte> _pendingToolUseIds = new();

    // The profile's always-allow rules, loaded on start and grown as the operator picks "always".
    // Registered with the coordinator per tool_use so it can short-circuit prompts already opted out of.
    private PermissionRuleSet _permissionRules = new();

    private Task? _pumpTask;
    private CancellationTokenSource? _pumpCancellation;
    private string? _sessionId;

    public ClaudeCliSession(
        IClaudeCliProcess process,
        IPermissionCoordinator permissionCoordinator,
        IPermissionRuleStore permissionRuleStore,
        ILogger<ClaudeCliSession> logger)
    {
        _process = process;
        _permissionCoordinator = permissionCoordinator;
        _permissionRuleStore = permissionRuleStore;
        _logger = logger;
    }

    public string? SessionId => _sessionId;

    public ClaudeProfile? Profile { get; private set; }

    public IAsyncEnumerable<ClaudeSessionEvent> Events => _events.Reader.ReadAllAsync();

    public async Task StartAsync(ClaudeProfile? profile = null, string? permissionMode = null, string? model = null, CancellationToken cancellationToken = default)
    {
        Profile = profile;

        var savedRules = await _permissionRuleStore.LoadAsync(profile?.Label, cancellationToken).ConfigureAwait(false);
        _permissionRules = new PermissionRuleSet(savedRules);

        _process.Start(profile, permissionMode, model);
        _pumpCancellation = new CancellationTokenSource();
        _pumpTask = PumpOutputAsync(_pumpCancellation.Token);
    }

    public async Task SendUserMessageAsync(string text, IReadOnlyList<ImageAttachment>? images = null, CancellationToken cancellationToken = default)
    {
        // Wire shape per https://code.claude.com/docs/en/agent-sdk/streaming-vs-single-mode.md:
        // {"type":"user","message":{"role":"user","content":"..."}}
        // One user-message object per stdin line keeps the same persistent session/turn loop alive.
        // With attachments, content becomes an array of blocks (text + one image block per attachment) —
        // shape verified against claude.exe 2.1.197. Text-only keeps the plain-string content.
        object content = images is { Count: > 0 }
            ? _BuildContentBlocks(text, images)
            : text;

        var payload = new
        {
            type = "user",
            message = new
            {
                role = "user",
                content,
            },
        };

        var line = JsonSerializer.Serialize(payload);
        await _process.WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
    }

    private static object[] _BuildContentBlocks(string text, IReadOnlyList<ImageAttachment> images)
    {
        var blocks = new List<object> { new { type = "text", text } };
        foreach (var image in images)
        {
            blocks.Add(new
            {
                type = "image",
                source = new
                {
                    type = "base64",
                    media_type = image.MediaType,
                    data = image.Base64Data,
                },
            });
        }

        return [.. blocks];
    }

    public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default)
    {
        var decision = allow
            ? PermissionDecision.Allow()
            : PermissionDecision.Deny("Denied by the cockpit operator.");

        var resolved = _permissionCoordinator.Resolve(toolUseId, decision);
        _pendingToolUseIds.TryRemove(toolUseId, out _);

        if (!resolved)
        {
            // The CLI auto-allowed this tool (never prompted) or the request already resolved —
            // the UI affordance was optimistic. Nothing to feed back in-band; just note it.
            _logger.LogInformation(
                "Permission decision had no pending CLI request to resolve: tool_use_id={ToolUseId} allow={Allow}",
                toolUseId,
                allow);
        }

        return Task.CompletedTask;
    }

    public async Task AllowPermissionAlwaysAsync(
        string toolUseId,
        string toolName,
        string proposedInputJson,
        PermissionRuleScope scope,
        CancellationToken cancellationToken = default)
    {
        var rule = scope == PermissionRuleScope.Wildcard
            ? PermissionRule.ForWildcard(toolName)
            : PermissionRule.ForExact(toolName, proposedInputJson);

        // Add to the live set first so any in-flight/immediately-following call for this profile is
        // covered even before the (slower) disk write finishes, then persist for next launch.
        if (_permissionRules.Add(rule))
        {
            await _permissionRuleStore.AddAsync(Profile?.Label, rule, cancellationToken).ConfigureAwait(false);
        }

        await RespondToPermissionAsync(toolUseId, allow: true, cancellationToken).ConfigureAwait(false);
    }

    public Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default) =>
        _SendControlRequestAsync(new { subtype = "set_permission_mode", mode }, cancellationToken);

    public Task SetModelAsync(string? model, CancellationToken cancellationToken = default) =>
        _SendControlRequestAsync(new { subtype = "set_model", model }, cancellationToken);

    public Task SetMaxThinkingTokensAsync(int maxThinkingTokens, CancellationToken cancellationToken = default) =>
        _SendControlRequestAsync(new { subtype = "set_max_thinking_tokens", maxThinkingTokens }, cancellationToken);

    public Task InterruptAsync(CancellationToken cancellationToken = default) =>
        _SendControlRequestAsync(new { subtype = "interrupt" }, cancellationToken);

    /// <summary>
    /// Builds and writes a single <c>control_request</c> line:
    /// <c>{"type":"control_request","request_id":"...","request":{"subtype":"...", ...}}</c>.
    /// This wire shape is verified against claude 2.1.197 (each subtype returns a
    /// <c>control_response</c> success). The matching <c>control_response</c> is not correlated back
    /// to the caller here; it is logged, not awaited/blocked on, per the F-C1 driver scope.
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

            // Control-protocol replies to our own SetPermissionModeAsync/SetModelAsync/
            // SetMaxThinkingTokensAsync/InterruptAsync control_requests (see _SendControlRequestAsync).
            // Logged only, per F-C1 scope: nothing here currently correlates a response back to its
            // request_id or blocks on it.
            if (root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
                && typeProp.GetString() is "control_response" or "control_cancel_request")
            {
                _logger.LogInformation("Received {Type} line: {Line}", typeProp.GetString(), line);
                return Task.CompletedTask;
            }

            // A single line can carry multiple events (e.g. an assistant snapshot with several
            // content blocks). Each tool_use also gets a derived PermissionRequested and has its
            // id tracked so the MCP permission call (which arrives without a session_id) can be
            // correlated to this session on tool_use_id, and any still-pending ones denied on dispose.
            foreach (var evt in ClaudeStreamJsonParser.ParseLine(line))
            {
                _events.Writer.TryWrite(evt);
                if (evt is ToolUseRequested toolUse)
                {
                    _pendingToolUseIds.TryAdd(toolUse.ToolUseId, 0);

                    // Hand the profile's always-allow rules to the coordinator keyed on this id, so a
                    // matching rule short-circuits the MCP prompt (which arrives without a session_id)
                    // straight to allow instead of surfacing a decision the operator already made.
                    _permissionCoordinator.RegisterToolUse(toolUse.ToolUseId, _permissionRules);

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
        // Unblock any tool call still waiting on an answer, or the disposing CLI process hangs.
        _permissionCoordinator.DenyPending(_pendingToolUseIds.Keys, "Session closed before the operator responded.");
        _pendingToolUseIds.Clear();

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
