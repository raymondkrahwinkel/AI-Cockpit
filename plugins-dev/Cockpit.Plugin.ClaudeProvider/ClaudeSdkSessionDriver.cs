using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// <see cref="IPluginSessionDriver"/> for the headless <c>claude</c> CLI over the persistent stream-json protocol
/// (Fase 4, SDK route, weg A) — the plugin-owned analogue of the host's <c>ClaudeCliSession</c>. One long-lived process
/// hosts the whole multi-turn conversation; each <see cref="SendUserMessageAsync"/> writes one user-message line and the
/// streamed <c>assistant</c>/<c>stream_event</c>/<c>result</c> lines are mapped to plugin events by
/// <see cref="ClaudeStreamJson"/>.
/// </summary>
/// <remarks>
/// Permissions ride the <em>control protocol</em>, not an HTTP MCP server (<see cref="ClaudeControlProtocol"/>): the CLI
/// is spawned without <c>--permission-prompt-tool</c>, an <c>initialize</c> control_request puts the cockpit on the
/// control channel, and every tool needing approval arrives as a <c>can_use_tool</c> control_request which this driver
/// surfaces as <see cref="PluginPermissionRequested"/> and answers with a control_response — the exact same in-band
/// shape Codex's app-server route uses for <c>item/*/requestApproval</c>. No logged-in <c>claude</c> exists in this
/// sandbox, so the live end (the CLI emitting <c>can_use_tool</c> for this spawn) is Raymond's eyeball item; the
/// turn-taking and the parse/respond round-trip are unit-tested against a fake subprocess.
/// </remarks>
internal sealed class ClaudeSdkSessionDriver : IPluginSessionDriver
{
    /// <summary>
    /// Option key for the per-session permission mode (also a live control) — the well-known key the host's driver
    /// adapter folds its typed permission-mode selection into, so a launch-time choice (bypass, plan, …) actually
    /// reaches this driver instead of falling back to the default.
    /// </summary>
    public const string PermissionModeOptionKey = WellKnownPluginSessionOptions.PermissionMode;

    /// <summary>Option key for the per-session model override (also a live control, #45 D4).</summary>
    public const string ModelOptionKey = "model";

    // The CLI's four real --permission-mode launch values (matching the host's SessionOptionCatalog.AllPermissionModes;
    // there is no "auto" mode — the CLI rejects it).
    private static readonly IReadOnlyList<string> _PermissionModes = ["default", "acceptEdits", "plan", "bypassPermissions"];

    // The modes a live set_permission_mode control request can actually switch to. bypassPermissions is launch-only —
    // the CLI cannot enter it mid-session — so it is not offered as a live switch (mirrors the host's LivePermissionModes).
    private static readonly IReadOnlyList<string> _LivePermissionModes = ["default", "acceptEdits", "plan"];
    private static readonly IReadOnlyList<string> _ModelSuggestions = ["opus", "sonnet", "haiku"];

    private readonly Func<IClaudeSdkSubprocess> _subprocessFactory;
    private readonly ClaudeProviderConfig _config;
    private readonly string _executablePath;
    private readonly Channel<PluginSessionEvent> _events = Channel.CreateUnbounded<PluginSessionEvent>();
    private readonly CancellationTokenSource _lifetime = new();

    // tool_use_id -> the pending can_use_tool request the CLI is blocking on. Keyed on tool_use_id because that is what
    // the transcript card (and therefore the UI's decision) is correlated on; the request_id is the wire correlation the
    // response must carry, and the original input must ride back verbatim as updatedInput on an allow — sending an empty
    // object there would make the CLI run the tool with no arguments (Bash with no command, Write with no content, …).
    private readonly ConcurrentDictionary<string, (string RequestId, string InputJson)> _pendingApprovals = new();

    private IClaudeSdkSubprocess? _subprocess;
    private Task? _stdoutPump;
    private Task? _stderrDrain;
    private string? _sessionId;
    private string? _model;
    private IReadOnlyList<PluginSessionLaunchOption> _liveOptions = [];

    public ClaudeSdkSessionDriver(Func<IClaudeSdkSubprocess> subprocessFactory, ClaudeProviderConfig config, string executablePath)
    {
        _subprocessFactory = subprocessFactory;
        _config = config;
        _executablePath = executablePath;
    }

    public PluginSessionCapabilities Capabilities { get; } = new(SupportsTools: true, SupportsPermissions: true, SupportsVision: true);

    public string? SessionId => _sessionId;

    public int? ProcessId => _subprocess?.ProcessId;

    public IReadOnlyList<PluginSessionLaunchOption> LiveOptions => _liveOptions;

    public IAsyncEnumerable<PluginSessionEvent> Events => _events.Reader.ReadAllAsync();

    public Task StartAsync(string? model = null, CancellationToken cancellationToken = default) =>
        StartAsync(model, workingDirectory: null, resumeSessionId: null, options: null, mcpServers: null, cancellationToken);

    public async Task StartAsync(string? model, string? workingDirectory, string? resumeSessionId, IReadOnlyDictionary<string, string>? options, IReadOnlyList<PluginMcpServer>? mcpServers, CancellationToken cancellationToken)
    {
        // A per-session option the operator picked in the New-session dialog wins over the model parameter, which wins
        // over the profile's own default; likewise the permission mode.
        var permissionMode = _ResolveOption(options, PermissionModeOptionKey, defaultValue: "default") ?? "default";
        var effectiveModel = _ResolveOption(options, ModelOptionKey, model);
        _model = effectiveModel;

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var resolvedWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? Environment.CurrentDirectory
            : workingDirectory;
        var configJsonDirectory = ClaudeConfigPaths.ResolveConfigJsonDirectory(_config.ConfigDir, userHome);

        // Trust must land before the process starts, or the headless CLI blocks on its interactive trust dialog with
        // nothing able to answer it — in the .claude.json the CLI reads for this spawn.
        ClaudeWorkspaceTrust.MarkWorkingDirectoryTrusted(configJsonDirectory, Path.GetFullPath(resolvedWorkingDirectory));

        var arguments = ClaudeSdkArguments.BuildArguments(permissionMode, effectiveModel, resumeSessionId, continueMostRecent: false);
        var environment = _BuildEnvironment(userHome);

        var subprocess = _subprocessFactory();
        _subprocess = subprocess;
        subprocess.Start(_executablePath, arguments, resolvedWorkingDirectory, environment);

        _stdoutPump = Task.Run(() => _PumpStdoutAsync(subprocess, _lifetime.Token), CancellationToken.None);
        // stderr must be drained or a full pipe deadlocks the child; its lines are diagnostic only.
        _stderrDrain = Task.Run(() => _DrainStderrAsync(subprocess, _lifetime.Token), CancellationToken.None);

        // Put an SDK client on the control channel so the CLI routes approvals here as can_use_tool requests. Sent
        // fire-and-forget (the reply is drained by the pump, not correlated), matching the host's control_request style.
        await subprocess.WriteLineAsync(ClaudeControlProtocol.BuildInitializeRequest(Guid.NewGuid().ToString()), cancellationToken).ConfigureAwait(false);

        _liveOptions = _BuildLiveOptions(effectiveModel, permissionMode);
    }

    public Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default) =>
        _SendUserMessageAsync(text, images: null, cancellationToken);

    public Task SendUserMessageAsync(string text, IReadOnlyList<PluginImageAttachment>? images, CancellationToken cancellationToken) =>
        _SendUserMessageAsync(text, images, cancellationToken);

    private async Task _SendUserMessageAsync(string text, IReadOnlyList<PluginImageAttachment>? images, CancellationToken cancellationToken)
    {
        // Wire shape per the Agent SDK streaming docs: {"type":"user","message":{"role":"user","content":...}}. One
        // user-message object per stdin line keeps the same persistent multi-turn session alive. With attachments the
        // content becomes an array of blocks (text + one image block per attachment) — shape verified against
        // claude.exe 2.1.197; text-only keeps the plain-string content.
        object content = images is { Count: > 0 } ? _BuildContentBlocks(text, images) : text;

        var payload = new
        {
            type = "user",
            message = new { role = "user", content },
        };

        await _RequireSubprocess().WriteLineAsync(JsonSerializer.Serialize(payload), cancellationToken).ConfigureAwait(false);
    }

    private static object[] _BuildContentBlocks(string text, IReadOnlyList<PluginImageAttachment> images)
    {
        var blocks = new List<object> { new { type = "text", text } };
        foreach (var image in images)
        {
            blocks.Add(new
            {
                type = "image",
                source = new { type = "base64", media_type = image.MediaType, data = image.Base64Data },
            });
        }

        return [.. blocks];
    }

    public Task InterruptAsync(CancellationToken cancellationToken = default) =>
        _SendControlRequestAsync(new { subtype = "interrupt" }, cancellationToken);

    public async Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default)
    {
        if (!_pendingApprovals.TryRemove(toolUseId, out var pending))
        {
            // The CLI auto-allowed this tool (never prompted) or the request already resolved — the UI affordance was
            // optimistic. Nothing to feed back over the control channel.
            return;
        }

        // The original input rides back verbatim as updatedInput on an allow — the cockpit never rewrites tool input,
        // and dropping it would run the tool with no arguments.
        var line = ClaudeControlProtocol.BuildDecisionResponse(pending.RequestId, allow, pending.InputJson, denyMessage: "Denied by the cockpit operator.");
        await _RequireSubprocess().WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
    }

    public Task SetLiveOptionAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        // Model and permission mode are switchable mid-session over the control protocol — the exact set_model /
        // set_permission_mode subtypes the host's ClaudeCliSession already round-trips against the live CLI. A key this
        // driver did not declare is contract drift, not an operator mistake, so it is ignored.
        return key switch
        {
            ModelOptionKey => _SetModelAndRemember(value, cancellationToken),
            PermissionModeOptionKey => _SendControlRequestAsync(new { subtype = "set_permission_mode", mode = value }, cancellationToken),
            _ => Task.CompletedTask,
        };
    }

    private Task _SetModelAndRemember(string value, CancellationToken cancellationToken)
    {
        _model = string.IsNullOrWhiteSpace(value) ? null : value;
        return _SendControlRequestAsync(new { subtype = "set_model", model = _model }, cancellationToken);
    }

    private async Task _SendControlRequestAsync(object request, CancellationToken cancellationToken)
    {
        var payload = new
        {
            type = ClaudeControlProtocol.ControlRequestType,
            request_id = Guid.NewGuid().ToString(),
            request,
        };

        await _RequireSubprocess().WriteLineAsync(JsonSerializer.Serialize(payload), cancellationToken).ConfigureAwait(false);
    }

    private async Task _PumpStdoutAsync(IClaudeSdkSubprocess subprocess, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var line in subprocess.ReadStdoutLinesAsync(cancellationToken).ConfigureAwait(false))
            {
                _HandleLine(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on dispose.
        }
        catch (Exception exception)
        {
            _events.Writer.TryWrite(new PluginSessionError { SessionId = _sessionId, Message = exception.Message });
        }
        finally
        {
            _events.Writer.TryComplete();
        }
    }

    private void _HandleLine(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // A malformed stream-json line is dropped rather than killing the whole pump — the session stays alive for
            // the well-formed lines around it.
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String)
            {
                _sessionId = sid.GetString();
            }

            var type = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
                ? typeProp.GetString()
                : null;

            // Control-protocol lines are the CLI's permission requests and the replies to our own control_requests —
            // routed here, never to the transcript parser.
            if (ClaudeControlProtocol.IsControlLine(type))
            {
                if (ClaudeControlProtocol.TryParsePermissionRequest(root, out var requestId, out var toolUseId, out var toolName, out var inputJson))
                {
                    _pendingApprovals[toolUseId] = (requestId, inputJson);
                    _events.Writer.TryWrite(new PluginPermissionRequested
                    {
                        SessionId = _sessionId,
                        ToolUseId = toolUseId,
                        ToolName = toolName,
                        InputJson = inputJson,
                    });
                }

                return;
            }

            foreach (var evt in ClaudeStreamJson.ParseLine(line))
            {
                _events.Writer.TryWrite(evt);
            }
        }
    }

    private async Task _DrainStderrAsync(IClaudeSdkSubprocess subprocess, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in subprocess.ReadStderrLinesAsync(cancellationToken).ConfigureAwait(false))
            {
                // Discard — draining is what keeps a verbose stderr from filling the pipe and deadlocking the child.
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on dispose.
        }
        catch (Exception)
        {
            // stderr is diagnostic; a read failure here must not surface as a session error.
        }
    }

    private Dictionary<string, string?> _BuildEnvironment(string userHome)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // Drop any inherited ANTHROPIC_* credential: inheriting one silently moves the session off the operator's
        // subscription and onto API-key billing (the same rule the host's spawn applies). Null tells the subprocess
        // seam to remove the variable from the child's environment.
        foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>()
                     .Where(name => name.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)))
        {
            environment[key] = null;
        }

        // A non-default profile dir is exported as CLAUDE_CONFIG_DIR; a default-dir profile clears any inherited value
        // so the CLI uses its native home-root config/login.
        if (ClaudeConfigPaths.ResolveSpawnOverride(_config.ConfigDir, userHome) is { } configDirOverride)
        {
            environment[ClaudeConfigPaths.EnvironmentVariable] = configDirOverride;
        }
        else
        {
            environment[ClaudeConfigPaths.EnvironmentVariable] = null;
        }

        return environment;
    }

    private IReadOnlyList<PluginSessionLaunchOption> _BuildLiveOptions(string? model, string permissionMode)
    {
        // The current model rides the choices so the panel opens on what the session is actually using, even when it is
        // a pinned model or snapshot the suggestion list omits.
        var modelChoices = new List<string>(_ModelSuggestions);
        if (model is { Length: > 0 } current && !modelChoices.Contains(current))
        {
            modelChoices.Insert(0, current);
        }

        var liveOptions = new List<PluginSessionLaunchOption>
        {
            new(ModelOptionKey, "Model", modelChoices, model),
        };

        // A session launched in bypassPermissions shows no permission-mode switch: the CLI cannot leave bypass live, and
        // you do not casually step down from it mid-session (the host locks the same dropdown). The three switchable
        // modes open on the launched one.
        if (!string.Equals(permissionMode, "bypassPermissions", StringComparison.Ordinal))
        {
            liveOptions.Add(new PluginSessionLaunchOption(PermissionModeOptionKey, "Permission mode", _LivePermissionModes, permissionMode));
        }

        return liveOptions;
    }

    private static string? _ResolveOption(IReadOnlyDictionary<string, string>? options, string key, string? defaultValue) =>
        options is not null && options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : defaultValue;

    private IClaudeSdkSubprocess _RequireSubprocess() =>
        _subprocess ?? throw new InvalidOperationException($"{nameof(StartAsync)} must be called before sending to the session.");

    public async ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        await _lifetime.CancelAsync().ConfigureAwait(false);

        // Guarded so _lifetime.Dispose always runs even if the subprocess teardown throws something other than the
        // InvalidOperationException its own DisposeAsync catches (e.g. a Win32Exception out of Process.Kill) — otherwise
        // the CancellationTokenSource leaks and the pump tasks stay unobserved.
        try
        {
            if (_subprocess is not null)
            {
                await _subprocess.DisposeAsync().ConfigureAwait(false);
            }

            foreach (var pump in new[] { _stdoutPump, _stderrDrain })
            {
                if (pump is not null)
                {
                    try
                    {
                        await pump.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected — the pumps observe the lifetime cancellation.
                    }
                }
            }
        }
        finally
        {
            _lifetime.Dispose();
        }
    }
}
