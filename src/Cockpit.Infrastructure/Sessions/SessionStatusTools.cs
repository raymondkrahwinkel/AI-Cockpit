using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Formatting;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Sessions;

// The MCP tools a session uses to say what it is working on (#AC-13, `mcp__cockpit-session__*`), and — since
// AC-1094 — to run a command without tying up its own turn while it does. Kept separate from the orchestrator
// server so a delegated sub-agent, denied those tools to stop it delegating further, can still use both; status+
// name share one tool (#AC-312) but only the statusline is binding.
internal sealed class SessionStatusTools(
    ISessionLabelSink labels,
    ITrackedCommandRunner runner,
    RunTracker tracker,
    IWorkspaceAgentGateway workspaces,
    IWorkspaceAgentCoordinator coordinator,
    IAgentMessageInbox inbox)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    // AC-1094: who a run's completion is delivered from — not a pane, the cockpit itself noticed this, same
    // convention as `CiWatcher.SenderPaneId`.
    private const string RunSenderPaneId = "cockpit-run";

    private const int MaxTimeoutSeconds = 900;

    [McpServerTool(Name = "set_status", ReadOnly = false, Destructive = false)]
    [Description("Sets your session's statusline — the short line shown under the session's name in the cockpit (its header and the sidebar), saying what you are working on right now: a ticket you picked up ('AC-13'), a phase, whatever the operator would want to see at a glance across their sessions. `session` is optional — over the normal MCP transport your session is identified automatically; pass the COCKPIT_PANE_ID environment variable only if you are told the automatic identification failed. An empty status clears the line. Optionally propose a `name` for the session too. Set it when you pick up a piece of work, and update or clear it as you move on.")]
    public async Task<string> SetStatusAsync(
        [Description("The status to show, e.g. 'AC-13' or 'reviewing the diff'. An empty string clears it — including when you are calling only to propose a name, so pass the status you want left standing rather than an empty one.")] string status,
        [Description("Optional. Your session id — the value of the COCKPIT_PANE_ID environment variable in this session. Only needed as a fallback when automatic session identification is unavailable.")] string? session = null,
        [Description("Optional. A name to propose for this session — the ticket you just picked up, say. It is taken only while the session still carries a name the cockpit made up for it; a name the operator gave it stays, and the reply says so with `renamed: false`. Leave it out to keep the current name.")] string? name = null)
    {
        // Key on the transport-verified pane (AC-89/AC-128), not the agent-declared `session`: an agent must not be
        // able to spoof or clear another session's statusline by naming its id (confused deputy). Falls back to
        // `session` off the verified path (the in-process tool loop / tests), where there is no middleware to trust.
        var caller = McpRequestContext.CurrentPaneId ?? session;
        if (string.IsNullOrEmpty(caller))
        {
            return JsonSerializer.Serialize(
                new { ok = false, error = "Could not identify your session and no `session` was given — pass the COCKPIT_PANE_ID environment variable as `session`." },
                SerializerOptions);
        }

        var applied = await labels.SetStatuslineAsync(caller, status ?? string.Empty);
        if (!applied)
        {
            return JsonSerializer.Serialize(
                new { ok = false, error = "No session matched that id — pass the COCKPIT_PANE_ID from this session's own environment as `session`." },
                SerializerOptions);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return JsonSerializer.Serialize(new { ok = true, status = status ?? string.Empty }, SerializerOptions);
        }

        // The same verified pane, so a name cannot be pushed onto a session an agent merely names either.
        var renamed = await labels.SuggestNameAsync(caller, name);
        return JsonSerializer.Serialize(new { ok = true, status = status ?? string.Empty, renamed }, SerializerOptions);
    }

    [McpServerTool(Name = "start_run", ReadOnly = false, Destructive = false)]
    [Description("DRAFT — under review, wording not final. Starts a command (e.g. a `dotnet test` filter) and returns a run id right away, before the command has finished — you can end your turn immediately after. When it ends, by finishing or by its timeout, the verdict is delivered to your own inbox and, if you have wake consent, a turn is started for you to read it; either way it never restarts anything on your behalf. There is no lock and no queue: an unrelated concurrent run of your own or another session's is not something this waits on or refuses over. On timeout the verdict is `TimedOut`, carrying whatever the command had already printed. Every process the run's tree still holds when it ends — a compiler daemon it spawned and left running included — is ended too, whether the run finished cleanly or timed out; this does not touch anything outside that one run. `unreachable`, when present in the reply, means nothing is going to bring this run's verdict to you on its own — read it before you end your turn.")]
    public async Task<string> StartRunAsync(
        [Description("The directory the command runs in.")] string workingDirectory,
        [Description("The command to run, e.g. 'dotnet'. Never parsed by a shell — pass its arguments separately in `arguments`, not appended here.")] string command,
        [Description("The command's arguments, e.g. ['test', '--filter', 'FullyQualifiedName~FooTests']. Each is passed through exactly as given, never re-parsed by a shell.")] string[]? arguments = null,
        [Description("How long the command may run before it is ended and the verdict is TimedOut, in seconds. Defaults to 900 and cannot be set higher.")] int timeoutSeconds = MaxTimeoutSeconds,
        [Description("Optional. Your session id — the value of the COCKPIT_PANE_ID environment variable in this session. Only needed as a fallback when automatic session identification is unavailable.")] string? session = null)
    {
        var caller = McpRequestContext.CurrentPaneId ?? session;
        if (string.IsNullOrEmpty(caller))
        {
            return _Error("Could not identify your session and no `session` was given — pass the COCKPIT_PANE_ID environment variable as `session`.");
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return _Error("`command` is required.");
        }

        var effectiveArguments = arguments ?? [];
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, MaxTimeoutSeconds));
        var runId = Guid.NewGuid().ToString("N");
        var unreachable = await _UnreachableAtStartAsync(caller).ConfigureAwait(false);

        using var stopping = new CancellationTokenSource();
        var label = effectiveArguments.Length is 0 ? command : $"{command} {string.Join(' ', effectiveArguments)}";
        tracker.Begin(runId, label, DateTimeOffset.UtcNow, () =>
        {
            stopping.Cancel();
            return Task.CompletedTask;
        });

        // Fire-and-forget by design: the whole point is that the caller does not wait on this. `stopping` is
        // disposed by the continuation itself, once the run and its cleanup are both over.
        _ = _RunAndDeliverAsync(caller, runId, label, workingDirectory, command, effectiveArguments, timeout, stopping);

        return JsonSerializer.Serialize(new { ok = true, runId, unreachable }, SerializerOptions);
    }

    [McpServerTool(Name = "run_status", ReadOnly = true)]
    [Description("DRAFT — under review, wording not final. Reports what a run started with start_run is doing, or what it finished with — the recovery path for when that run's inbox delivery never reached you (a wake refused, an inbox already full, a turn that ended before it arrived). Never starts or restarts anything.")]
    public string RunStatus([Description("The run id start_run returned.")] string runId)
    {
        if (tracker.IsRunning(runId))
        {
            return JsonSerializer.Serialize(new { ok = true, runId, status = "running" }, SerializerOptions);
        }

        if (tracker.Get(runId) is { } record)
        {
            return JsonSerializer.Serialize(
                new
                {
                    ok = true,
                    runId,
                    status = "finished",
                    verdict = record.Result.TimedOut ? "TimedOut" : record.Result.ExitCode == 0 ? "Passed" : "Failed",
                    exitCode = record.Result.ExitCode,
                    seconds = Math.Round(record.Result.Duration.TotalSeconds, 1),
                    standardOutput = record.Result.StandardOutput,
                    standardError = record.Result.StandardError,
                    finishedAtUtc = record.FinishedAt,
                },
                SerializerOptions);
        }

        return _Error($"No run with id '{runId}' is known — either it never existed, or it finished long enough ago that its result aged out.");
    }

    private async Task _RunAndDeliverAsync(
        string caller,
        string runId,
        string label,
        string workingDirectory,
        string command,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationTokenSource stopping)
    {
        using (stopping)
        {
            TrackedRunResult result;
            try
            {
                result = await runner.RunAsync(workingDirectory, command, arguments, timeout, runId, stopping.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Fail-soft, same as every tool result here: whatever went wrong, the caller still gets a verdict
                // rather than a silently dropped run.
                result = new TrackedRunResult(-1, string.Empty, exception.Message, TimeSpan.Zero, TimedOut: false);
            }

            tracker.Complete(runId, result, DateTimeOffset.UtcNow);
            inbox.Deliver(RunSenderPaneId, caller, "run", _Verdict(runId, label, result));
        }
    }

    private static string _Verdict(string runId, string label, TrackedRunResult result)
    {
        var verdict = result.TimedOut ? "TimedOut" : result.ExitCode == 0 ? "Passed" : "Failed";
        var summary = $"Run {runId} ('{label}') finished: {verdict}. Exit code {result.ExitCode}, {Math.Round(result.Duration.TotalSeconds, 1)}s.";

        // The tail travels only off a clean pass — a whole test run's stdout in an inbox message is exactly the
        // context-window cost this tool exists to spare, and there is nothing to read in a passing one.
        if (verdict == "Passed")
        {
            return summary;
        }

        var tail = BoundedText.Trim(result.StandardOutput + result.StandardError, MaxVerdictTailLength);
        return tail.Length > 0 ? $"{summary}\n{tail}" : summary;
    }

    // AC-1094: a run's own turn-piggybacked mail already bounds a tool result; this bounds what one inbox message
    // can carry on top of that — a fifth of AgentsMcpTools.MaxMessagesPerRead's budget for one message.
    private const int MaxVerdictTailLength = 4000;

    private async Task<string?> _UnreachableAtStartAsync(string caller)
    {
        if (await workspaces.GetWorkspaceSnapshotAsync(caller).ConfigureAwait(false) is not { } snapshot)
        {
            return "This session could not be placed in a workspace, so there is no way to tell whether ending your turn now will bring the verdict back on its own. Call run_status(runId) yourself once you expect this to be done.";
        }

        return AgentsMcpTools._ReachableVia(coordinator, snapshot, caller) == AgentsMcpTools.ReachableOperatorOnly
            ? "Nothing is going to bring this run's verdict to you on its own: you have no turn-start delivery and have not opted in to being woken (set_wake_optin). If you end your turn now, call run_status(runId) yourself once you expect this to be done — it will not find you."
            : null;
    }

    private static string _Error(string message) =>
        JsonSerializer.Serialize(new { ok = false, error = message }, SerializerOptions);
}
