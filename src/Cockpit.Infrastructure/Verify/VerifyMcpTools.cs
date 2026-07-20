using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Verify;
using Cockpit.Core.Verify;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Verify;

/// <summary>
/// The <c>cockpit-verify</c> MCP tool (AC-86): closes the visual verify loop so UI work is not delivered blind
/// (Iron Law #9). The agent triggers the runner registered for its project; the tool runs that registered command
/// behind an operator consent, then hands back the text snapshot it produced on the tool result — the channel every
/// provider already reads — and, only for a screenshot a tool result cannot carry, feeds an image into the session.
/// <para>
/// The agent can only trigger a <em>registered</em> runner, never supply a command: the command lives in the
/// registry the operator wrote, so "verify" cannot become a path to arbitrary command execution. Each run is still
/// gated by the shared AC-47 consent broker (Dangerous, the verbatim command), which fails closed when no operator
/// can be asked.
/// </para>
/// </summary>
internal sealed class VerifyMcpTools
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private const int MaxSnapshotChars = 16000;
    private const int StandardErrorTailChars = 800;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly IVerifyRunnerRegistry _registry;
    private readonly IVerifySessionGateway _gateway;
    private readonly IVerifyCommandRunner _commandRunner;
    private readonly IConsentBroker? _consent;

    // The consent broker is optional so the tool's own tests construct it without a host; the container injects the
    // shared singleton, so a real run is gated behind an operator Approve/Deny that fails closed when nobody can ask.
    public VerifyMcpTools(
        IVerifyRunnerRegistry registry,
        IVerifySessionGateway gateway,
        IVerifyCommandRunner commandRunner,
        IConsentBroker? consent = null)
    {
        _registry = registry;
        _gateway = gateway;
        _commandRunner = commandRunner;
        _consent = consent;
    }

    [McpServerTool(Name = "verify")]
    [Description("Runs the visual verify loop for this session's project so you can see the UI you changed instead of guessing: it runs the verify command the operator registered for this project (you cannot choose the command — only trigger it), then hands the rendered UI back to you as a text snapshot on this tool result, and for image-capable providers also adds a screenshot to the session. Use it after a UI/layout change, before calling the work done. The operator approves the run each time. It runs for the session you call it from — you do not name one.")]
    public async Task<string> VerifyAsync()
    {
        // Identity comes from the authenticated request (AC-89), never from a value the agent could type — that closes
        // the confused deputy where an agent names another pane to run its command or push a screenshot into it. A
        // request with no verified pane (not a per-session-token caller) cannot be attributed, so verify refuses it.
        if (McpRequestContext.CurrentPaneId is not { } callerSession)
        {
            return _Serialize(new { ok = false, error = "This verify request could not be attributed to a session." });
        }

        if (_gateway.GetWorkingDirectory(callerSession) is not { } workingDirectory)
        {
            return _Serialize(new { ok = false, error = "This session is not one the cockpit can verify — verify works on an interactive session running in a project." });
        }

        if (_SelectRunner(await _registry.ListAsync().ConfigureAwait(false), workingDirectory) is not { } runner)
        {
            return _Serialize(new { ok = false, error = "No verify runner is registered for this session's project. Register one in the sidebar menu → Verify runners (a command that renders the UI to a snapshot file)." });
        }

        if (runner.CaptureType != VerifyCaptureType.Avalonia)
        {
            return _Serialize(new { ok = false, error = $"The verify runner \"{runner.Label}\" uses a capture type this build cannot read back yet." });
        }

        if (_consent is null)
        {
            return _Serialize(new { ok = false, error = "Running a verify command needs the operator's approval, which is not available here." });
        }

        var decision = await _consent.RequestConsentAsync(new ConsentRequest(
            "An agent wants to run a verify command",
            _ConsentAction(runner),
            new ConsentSource(callerSession, null, "Verify MCP"),
            "verify.run",
            ConsentRisk.Dangerous)).ConfigureAwait(false);
        if (!decision.IsApproved)
        {
            return _Serialize(new { ok = false, error = "Running the verify command was not approved by the operator." });
        }

        try
        {
            // Clear the previous run's output first, then only read a file this run actually wrote (written at or
            // after runStartedUtc). Together that means a command which fails before it writes fresh output — a broken
            // build exiting non-zero — can never have a leftover snapshot read back and vouched for as the current UI,
            // even if the delete itself was refused (a locked file). That is the one thing this loop exists to prevent
            // (Iron Law #9).
            _ClearStaleOutputs(runner);
            var runStartedUtc = DateTime.UtcNow;

            var result = await _commandRunner.RunAsync(runner).ConfigureAwait(false);

            var snapshotPath = _Resolve(runner.WorkingDirectory, runner.SnapshotPath);
            var snapshot = _WrittenSince(snapshotPath, runStartedUtc)
                ? await _ReadSnapshotAsync(snapshotPath).ConfigureAwait(false)
                : string.Empty;
            if (string.IsNullOrWhiteSpace(snapshot))
            {
                return _Serialize(new
                {
                    ok = false,
                    error = $"The verify command produced no snapshot at {runner.SnapshotPath}.",
                    exitCode = result.ExitCode,
                    timedOut = result.TimedOut,
                    stderr = _Tail(result.StandardError, StandardErrorTailChars),
                });
            }

            // The snapshot text rides back on this tool result — the channel every provider (SDK or TTY) already reads,
            // so nothing multi-line is ever typed into a pty. Only the screenshot, which a tool result cannot carry,
            // goes through the session feed, where a vision-capable SDK session shows it and everything else ignores it.
            var screenshotPath = _ResolveOptional(runner.WorkingDirectory, runner.ScreenshotPath);
            var screenshot = screenshotPath is not null && _WrittenSince(screenshotPath, runStartedUtc)
                ? await _ReadScreenshotAsync(screenshotPath).ConfigureAwait(false)
                : null;
            var screenshotShown = screenshot is not null
                && await _gateway.FeedResultAsync(callerSession, _ScreenshotCaption(runner), screenshot).ConfigureAwait(false);

            return _Serialize(new
            {
                ok = true,
                runner = runner.Label,
                exitCode = result.ExitCode,
                timedOut = result.TimedOut,
                durationSeconds = Math.Round(result.Duration.TotalSeconds, 1),
                screenshotShown,
                snapshot = _TrimForResult(snapshot),
                message = "This is the rendered UI as text — check it against what you intended before calling the work done (Iron Law #9)."
                    + (screenshotShown ? " A screenshot was also added to this session." : string.Empty),
            });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    // The runner whose directory is the session's working directory or an ancestor of it, longest (most specific)
    // match first — a session in a sub-folder still resolves to its project's runner. Entries with a blank directory
    // (only reachable by hand-editing the config, since the dialog rejects them) are skipped rather than thrown on.
    private static VerifyRunner? _SelectRunner(IReadOnlyList<VerifyRunner> runners, string workingDirectory)
    {
        var target = _Normalize(workingDirectory);
        return runners
            .Where(runner => !string.IsNullOrWhiteSpace(runner.WorkingDirectory) && _IsAtOrUnder(target, _Normalize(runner.WorkingDirectory)))
            .OrderByDescending(runner => runner.WorkingDirectory.Length)
            .FirstOrDefault();
    }

    private static bool _IsAtOrUnder(string candidate, string root) =>
        string.Equals(candidate, root, PathComparison)
        || candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);

    private static string _Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static void _ClearStaleOutputs(VerifyRunner runner)
    {
        _TryDelete(_Resolve(runner.WorkingDirectory, runner.SnapshotPath));
        _TryDelete(_ResolveOptional(runner.WorkingDirectory, runner.ScreenshotPath));
    }

    // The command runs in the runner's working directory and writes its output there, so a relative snapshot/screenshot
    // path is resolved against that directory — not the cockpit process's own working directory, where File.Exists
    // would otherwise look and never find it.
    private static string _Resolve(string workingDirectory, string path) =>
        Path.GetFullPath(path, Path.GetFullPath(workingDirectory));

    private static string? _ResolveOptional(string workingDirectory, string? path) =>
        string.IsNullOrEmpty(path) ? null : _Resolve(workingDirectory, path);

    private static void _TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort: even a stale file we cannot delete here is caught by the written-since-runStart check on
            // read, so it can never be vouched for as fresh output. Deleting first just keeps the common case tidy.
        }
    }

    // A small allowance for filesystem timestamp granularity (some filesystems round the last-write time down to the
    // nearest second), so a file the run genuinely wrote is never rejected as stale by a sub-second rounding.
    private static readonly TimeSpan FreshnessTolerance = TimeSpan.FromSeconds(2);

    // True only when the file exists and was last written around when the run started or later — so a leftover file
    // the run did not refresh (e.g. a delete the OS refused), which is always seconds-to-minutes older, is treated as
    // absent rather than read back as the current UI.
    private static bool _WrittenSince(string path, DateTime sinceUtc) =>
        File.Exists(path) && File.GetLastWriteTimeUtc(path) >= sinceUtc - FreshnessTolerance;

    private static async Task<string> _ReadSnapshotAsync(string path)
    {
        try
        {
            return File.Exists(path) ? await File.ReadAllTextAsync(path).ConfigureAwait(false) : string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static async Task<byte[]?> _ReadScreenshotAsync(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            // The screenshot is additive enrichment: a missing or unreadable one must not sink the text snapshot.
            return File.Exists(path) ? await File.ReadAllBytesAsync(path).ConfigureAwait(false) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string _TrimForResult(string snapshot) =>
        snapshot.Length > MaxSnapshotChars
            ? snapshot[..MaxSnapshotChars] + $"\n… (snapshot truncated at {MaxSnapshotChars} characters)"
            : snapshot;

    private static string _ScreenshotCaption(VerifyRunner runner) =>
        $"Screenshot of the rendered UI for verify runner \"{runner.Label}\" — the text snapshot is on the verify tool result.";

    private static string _ConsentAction(VerifyRunner runner)
    {
        var commandLine = string.Join(' ', new[] { runner.Command }.Concat(runner.Arguments));
        return $"Run verify runner {_SingleLine(runner.Label)}\n{_SingleLine(commandLine)}\nin {_SingleLine(runner.WorkingDirectory)}";
    }

    private static string _Tail(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[^maxChars..];

    // Fold any character a consent surface could render as a line break out of the runner's fields before they go
    // verbatim into the Dangerous prompt, so an odd stored value cannot smuggle in reassuring extra lines and bury
    // what the operator is approving (cf. AC-80/AC-92, the same flattening the terminal and worktree tools do). The
    // Unicode line/paragraph/next-line separators (0x2028/0x2029/0x0085) are compared numerically so no raw separator
    // sits in this source.
    private static string _SingleLine(string value) =>
        new(value.Select(character =>
            char.IsControl(character) || character == 0x2028 || character == 0x2029 || character == 0x0085
                ? ' '
                : character).ToArray());

    private static string _Serialize(object value) => JsonSerializer.Serialize(value, SerializerOptions);
}
