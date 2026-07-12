using System.Text;
using System.Threading.Channels;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider;

/// <summary>
/// <see cref="IPluginSessionDriver"/> for the Codex CLI provider, driven as a subprocess spawned fresh for
/// every turn (#45 fase B1) — the plugin-local mirror of <c>Cockpit.Infrastructure.Sessions.ClaudeCliSession</c>,
/// adapted for Codex's proces-per-turn headless mode instead of Claude's single persistent process.
/// </summary>
/// <remarks>
/// Lifecycle (design doc §2.1): <see cref="StartAsync"/> only records the model override — Codex's real
/// thread/session id is not known until the first turn's <c>thread.started</c> line arrives, so
/// <see cref="PluginSessionInitialized"/> is emitted lazily from <see cref="CodexJsonlEventMapper"/> rather
/// than synthesized up front. Each <see cref="SendUserMessageAsync"/> spawns a brand-new
/// <see cref="ICliSubprocess"/> (turn 1: <c>codex exec --json "text"</c>; turn 2+:
/// <c>codex exec resume &lt;threadId&gt; --json "text"</c>, using the thread id captured from the previous
/// turn's <c>thread.started</c>), pumps its stdout through the mapper, and disposes it once the turn ends —
/// whether that is a natural <c>turn.completed</c>/<c>turn.failed</c>, an exception, or <see cref="InterruptAsync"/>
/// killing it mid-turn. A dedicated stderr-drain task runs alongside the stdout pump for every turn: Codex
/// writes progress to stderr, and an undrained stderr pipe would eventually fill and deadlock the child
/// (design doc §4) — this driver never blocks stdout on stderr or vice versa.
/// </remarks>
internal sealed class CliSubprocessPluginSessionDriver : IPluginSessionDriver
{
    private readonly Func<ICliSubprocess> _subprocessFactory;
    private readonly CliAgentConfig _config;
    private readonly string _executablePath;
    private readonly Channel<PluginSessionEvent> _events = Channel.CreateUnbounded<PluginSessionEvent>();

    private string? _model;
    private string? _sessionId;
    private ICliSubprocess? _currentSubprocess;
    private CancellationTokenSource? _turnCancellation;

    public CliSubprocessPluginSessionDriver(Func<ICliSubprocess> subprocessFactory, CliAgentConfig config, string executablePath)
    {
        _subprocessFactory = subprocessFactory;
        _config = config;
        _executablePath = executablePath;
        _model = config.Model;
    }

    public PluginSessionCapabilities Capabilities { get; } = new(SupportsTools: true, SupportsPermissions: false);

    public string? SessionId => _sessionId;

    public IAsyncEnumerable<PluginSessionEvent> Events => _events.Reader.ReadAllAsync();

    public Task StartAsync(string? model = null, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            _model = model;
        }

        return Task.CompletedTask;
    }

    public Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default)
    {
        // The subprocess is created and recorded as "current" synchronously, right here — not inside the
        // background turn task below — so InterruptAsync can never race an in-flight SendUserMessageAsync
        // call: by the time this method returns, _currentSubprocess is already the instance the eventual
        // turn will spawn, whether or not that background task has actually started running yet.
        var subprocess = _subprocessFactory();
        _currentSubprocess = subprocess;

        // Run the turn in the background so the caller returns immediately and consumes the reply through
        // Events, mirroring both ClaudeCliSession's pump task and the Gemini plugin's own driver.
        _turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = _RunTurnAsync(subprocess, text, _turnCancellation.Token);
        return Task.CompletedTask;
    }

    private async Task _RunTurnAsync(ICliSubprocess subprocess, string text, CancellationToken cancellationToken)
    {
        var sawTurnCompletion = false;

        try
        {
            var arguments = BuildArguments(text);
            var environmentVariables = _BuildEnvironmentVariables();
            subprocess.Start(_executablePath, arguments, _config.WorkingDirectory, environmentVariables);

            if (_config.IsStdinPromptMode)
            {
                await subprocess.WriteLineAsync(text, cancellationToken).ConfigureAwait(false);
            }

            var stderrTask = _DrainStderrAsync(subprocess, cancellationToken);

            await foreach (var line in subprocess.ReadStdoutLinesAsync(cancellationToken).ConfigureAwait(false))
            {
                var result = CodexJsonlEventMapper.ParseLine(line, _sessionId);
                _sessionId = result.SessionId;
                foreach (var evt in result.Events)
                {
                    _events.Writer.TryWrite(evt);
                    if (evt is PluginTurnCompleted)
                    {
                        sawTurnCompletion = true;
                    }
                }
            }

            var stderrText = await stderrTask.ConfigureAwait(false);

            if (!sawTurnCompletion)
            {
                // Stdout hit EOF (the process exited) without ever emitting turn.completed/turn.failed —
                // either InterruptAsync killed it mid-turn, or it crashed/exited abnormally on its own.
                _EmitMissingTurnCompletion(subprocess, cancellationToken, stderrText);
            }
        }
        catch (OperationCanceledException)
        {
            _events.Writer.TryWrite(new PluginTurnCompleted { SessionId = _sessionId, Subtype = "interrupted", Result = null, IsError = false, StopReason = "interrupt" });
        }
        catch (Exception ex)
        {
            _events.Writer.TryWrite(new PluginSessionError { SessionId = _sessionId, Message = ex.Message });
            _events.Writer.TryWrite(new PluginTurnCompleted { SessionId = _sessionId, Subtype = "error", Result = null, IsError = true });
        }
        finally
        {
            await subprocess.DisposeAsync().ConfigureAwait(false);
            if (ReferenceEquals(_currentSubprocess, subprocess))
            {
                _currentSubprocess = null;
            }
        }
    }

    private void _EmitMissingTurnCompletion(ICliSubprocess subprocess, CancellationToken cancellationToken, string stderrText)
    {
        var wasInterrupted = cancellationToken.IsCancellationRequested;
        if (!wasInterrupted)
        {
            var detail = string.IsNullOrWhiteSpace(stderrText) ? string.Empty : $" stderr: {stderrText}";
            _events.Writer.TryWrite(new PluginSessionError
            {
                SessionId = _sessionId,
                Message = $"codex exited (code {subprocess.ExitCode?.ToString() ?? "unknown"}) without completing the turn.{detail}",
            });
        }

        _events.Writer.TryWrite(wasInterrupted
            ? new PluginTurnCompleted { SessionId = _sessionId, Subtype = "interrupted", Result = null, IsError = false, StopReason = "interrupt" }
            : new PluginTurnCompleted { SessionId = _sessionId, Subtype = "error", Result = null, IsError = true });
    }

    private static async Task<string> _DrainStderrAsync(ICliSubprocess subprocess, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        try
        {
            await foreach (var line in subprocess.ReadStderrLinesAsync(cancellationToken).ConfigureAwait(false))
            {
                builder.AppendLine(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when InterruptAsync cancels the shared turn token.
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Builds the CLI argument list for one turn. Extracted (and testable in isolation) so the resume-vs-first-turn
    /// and prompt-mode branching is unit-testable without spawning a real process — mirrors
    /// <c>ClaudeCliProcess.BuildArguments</c> being <see langword="internal static"/> for the same reason.
    /// </summary>
    internal IReadOnlyList<string> BuildArguments(string text)
    {
        var arguments = new List<string> { _config.SubCommand };

        if (_sessionId is { Length: > 0 } sessionId)
        {
            arguments.Add("resume");
            arguments.Add(sessionId);
        }

        arguments.AddRange(_config.EffectiveOutputFormatArgs);

        if (!string.IsNullOrWhiteSpace(_config.SandboxMode))
        {
            arguments.Add("--sandbox");
            arguments.Add(_config.SandboxMode);
        }

        if (!string.IsNullOrWhiteSpace(_model))
        {
            arguments.Add("-m");
            arguments.Add(_model);
        }

        arguments.AddRange(_config.EffectiveExtraArgs);

        if (!_config.IsStdinPromptMode)
        {
            arguments.Add(text);
        }

        return arguments;
    }

    private Dictionary<string, string?> _BuildEnvironmentVariables()
    {
        var environmentVariables = new Dictionary<string, string?>();

        if (!string.IsNullOrWhiteSpace(_config.AuthEnvVar) && !string.IsNullOrEmpty(_config.ApiKey))
        {
            // The key is set as an env-var for this one spawn only — never as a CLI argument (visible in the
            // process list) and never logged (CliAgentConfig.ToString() masks it too).
            environmentVariables[_config.AuthEnvVar] = _config.ApiKey;
        }

        if (!string.IsNullOrWhiteSpace(_config.ConfigDir))
        {
            environmentVariables["CODEX_HOME"] = _config.ConfigDir;
        }

        return environmentVariables;
    }

    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        _turnCancellation?.Cancel();

        // Codex/Gemini headless offer no in-band cancel message (design doc §1.3/§2.1) — interrupting a
        // proces-per-turn spawn means killing the child outright, not asking it nicely.
        if (_currentSubprocess is { } subprocess)
        {
            await subprocess.DisposeAsync().ConfigureAwait(false);
        }
    }

    // No in-band permission-prompt channel behind headless Codex (design doc §2.4) — a real no-op, not a throw,
    // so a host that calls it speculatively (Capabilities.SupportsPermissions is false, so it shouldn't) stays safe.
    public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        _turnCancellation?.Cancel();
        _turnCancellation?.Dispose();

        if (_currentSubprocess is { } subprocess)
        {
            await subprocess.DisposeAsync().ConfigureAwait(false);
        }
    }
}
