using System.Runtime.CompilerServices;
using System.Text;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// The Claude plugin's own transcript reader (weg A) for the host's read-aloud (#35b) and status (#39): a TTY
/// session runs the real interactive TUI, so there is no parsed event stream — but <c>claude</c> writes every
/// session live to <c>&lt;config-dir&gt;/projects/&lt;cwd-hash&gt;/&lt;session-id&gt;.jsonl</c>, so tailing that
/// file gets the assistant's text cleanly without touching the ANSI/TUI stream. Ported from the host's former
/// in-tree reader so the core carries no Claude-format knowledge; the config directory is resolved from this
/// plugin's own opaque <c>ConfigJson</c> rather than a host-supplied path.
/// <para>
/// The session id is <em>not</em> forced on the launch (undocumented for interactive sessions and does not
/// persist a transcript), so the file is identified as the new transcript that appears after launch — see
/// <see cref="SnapshotTranscripts"/>. It is tailed from its current end via manual byte-level buffering rather
/// than <see cref="StreamReader.ReadLine"/>, which cannot tell a real end-of-file apart from "more is coming"
/// and would emit a partial line the writer has not finished; a stateful <see cref="Decoder"/> carries any
/// UTF-8 multi-byte sequence split across a poll boundary.
/// </para>
/// </summary>
internal sealed class ClaudeTranscriptReader : IPluginTranscriptReader
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public IReadOnlySet<string> SnapshotTranscripts(string configJson) =>
        EnumerateTranscripts(_ResolveStateDirectory(configJson)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // AC-276 replaced a 30-second mtime window over <session>/subagents/agent-*.jsonl with the count the CLI
    // states itself on each turn's turn_duration line. The window was not merely imprecise, it was wrong most of
    // the time: measured over 547 real sub-agent transcripts, 82.8% fell silent for longer than 30s at least once
    // while still running (median longest silence 56s, p95 368s, max 3142s). Every one of those silences read as
    // "finished" and dropped the session to Done until the agent wrote again — the reported flicker. A thinking
    // pause is indistinguishable from completion by mtime alone, so no choice of window fixes it.

    public async IAsyncEnumerable<string> ReadAssistantTextAsync(
        string configJson,
        IReadOnlySet<string> knownTranscriptsAtLaunch,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var reading in ReadActivityAsync(configJson, knownTranscriptsAtLaunch, cancellationToken).ConfigureAwait(false))
        {
            if (reading.RawLine is { } line && ClaudeTranscriptLineParser.TryExtractAssistantText(line, out var assistantText))
            {
                yield return assistantText;
            }
        }
    }

    public async IAsyncEnumerable<PluginTranscriptActivity> ReadActivityAsync(
        string configJson,
        IReadOnlySet<string> knownTranscriptsAtLaunch,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var configDir = _ResolveStateDirectory(configJson);
        var transcriptPath = await _WaitForNewTranscriptAsync(configDir, knownTranscriptsAtLaunch, cancellationToken).ConfigureAwait(false);
        if (transcriptPath is null)
        {
            yield break;
        }

        await using var stream = new FileStream(
            transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        // Tail from the current end: whatever the session already wrote before this call is history,
        // not new activity — only lines appended from here on are new turns.
        stream.Seek(0, SeekOrigin.End);

        var decoder = Encoding.UTF8.GetDecoder();
        var readBuffer = new byte[8192];
        var charBuffer = new char[readBuffer.Length];
        var pendingLine = new StringBuilder();
        var mainTurnComplete = false;
        var lastEmitted = PluginSessionActivity.None;
        // Work that outlives the turn (AC-276), read from two different signals because the CLI reports them
        // differently. Sub-agents: a count it states itself on every turn_duration line, so this only ever mirrors
        // what the provider last said. Shells: no such total exists, so those are tallied by id from their own
        // start/end lines — see ClaudeTranscriptLineParser for why that asymmetry is safe here.
        var pendingSubAgents = 0;
        var outstandingShells = new HashSet<string>(StringComparer.Ordinal);
        // The CLI can write more than one transcript line for the same assistant API response (progressive
        // content-block saves) — every repeat carries the identical stop_reason/usage as the first, so treating
        // a repeat as its own turn-complete/usage reading would double (sometimes 2-3x) count one real API call.
        string? lastAssistantMessageId = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var bytesRead = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead <= 0)
            {
                if (pendingSubAgents > 0)
                {
                    // The main agent is quiet but a sub-agent is still running — keep the session off "done"
                    // and shown as background work, re-emitted each poll so the host's safety timeout never fires.
                    lastEmitted = PluginSessionActivity.BackgroundBusy;
                    yield return new PluginTranscriptActivity(PluginSessionActivity.BackgroundBusy, null, null, outstandingShells.Count);
                }
                else if (lastEmitted == PluginSessionActivity.BackgroundBusy)
                {
                    // The background work just ended (the agent finished or was killed); move off "working" to the
                    // main agent's own state, so the dot does not stay stuck on background after the sub-agent is gone.
                    lastEmitted = mainTurnComplete ? PluginSessionActivity.TurnComplete : PluginSessionActivity.Busy;
                    yield return new PluginTranscriptActivity(lastEmitted, null, null, outstandingShells.Count);
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var charCount = decoder.GetChars(readBuffer, 0, bytesRead, charBuffer, 0);
            var chunkStart = 0;
            for (var i = 0; i < charCount; i++)
            {
                if (charBuffer[i] != '\n')
                {
                    continue;
                }

                pendingLine.Append(charBuffer, chunkStart, i - chunkStart);
                chunkStart = i + 1;

                var line = pendingLine.ToString();
                pendingLine.Clear();

                var activity = ClassifyLine(line);
                ClaudeTranscriptLineParser.TryExtractUsage(line, out var usage, out var messageId);
                if (messageId is not null)
                {
                    if (messageId == lastAssistantMessageId)
                    {
                        // A repeat of the same API response already counted above — suppress both the turn
                        // transition and the usage so this line contributes nothing a second time.
                        activity = PluginSessionActivity.None;
                        usage = null;
                    }
                    else
                    {
                        lastAssistantMessageId = messageId;
                    }
                }

                if (activity == PluginSessionActivity.TurnComplete)
                {
                    mainTurnComplete = true;
                }
                else if (activity == PluginSessionActivity.Busy)
                {
                    mainTurnComplete = false;
                }

                // A backgrounded shell opening or closing (AC-276). Tracked by id so a repeated line cannot
                // double-count, and so an end for something never seen starting is simply ignored.
                if (ClaudeTranscriptLineParser.TryReadBackgroundShellTransition(line, out var shellId, out var shellStarted))
                {
                    if (shellStarted)
                    {
                        outstandingShells.Add(shellId);
                    }
                    else
                    {
                        // The same notification shape closes sub-agents too, but an end naming an id this reader
                        // never saw start is not evidence of anything: the tail begins at the end of the file, so a
                        // session resumed mid-turn legitimately sees ends for work it never saw begin. Inferring a
                        // sub-agent finished from that would decrement a count belonging to a different, still-live
                        // agent — reintroducing this ticket's own flicker by another route. Sub-agents are left
                        // entirely to the count the CLI restates each turn.
                        outstandingShells.Remove(shellId);
                    }
                }

                // The CLI closes each turn with its own count of sub-agents still running. It arrives just after
                // the assistant line that ends the turn (measured: 2476 of 2476, median 1.1s later), so this is
                // where a turn that looked complete is corrected into background work.
                if (ClaudeTranscriptLineParser.TryReadPendingSubAgentCount(line, out var stated))
                {
                    pendingSubAgents = stated;
                    if (stated > 0 && mainTurnComplete)
                    {
                        activity = PluginSessionActivity.BackgroundBusy;
                    }
                }

                // A completed main turn while a sub-agent still runs is background work, not done — the dot
                // should read "working (background)" until the agent itself ends.
                var emit = activity == PluginSessionActivity.TurnComplete && pendingSubAgents > 0
                    ? PluginSessionActivity.BackgroundBusy
                    : activity;
                if (emit != PluginSessionActivity.None)
                {
                    lastEmitted = emit;
                }

                yield return new PluginTranscriptActivity(emit, line, usage, outstandingShells.Count);
            }

            pendingLine.Append(charBuffer, chunkStart, charCount - chunkStart);
        }
    }

    /// <summary>
    /// Classifies one main-transcript JSONL line into a coarse turn-activity (ported from the host's former
    /// <c>TtyTranscriptStatus</c> so the Claude-format knowledge lives with the provider): a user message or a
    /// tool-result means the model owes a response (Busy); an assistant message is Busy while it streams or loops
    /// into a tool call and <see cref="PluginSessionActivity.TurnComplete"/> on a terminal stop_reason; anything
    /// else carries no signal.
    /// </summary>
    internal static PluginSessionActivity ClassifyLine(string? jsonLine)
    {
        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            return PluginSessionActivity.None;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(jsonLine);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                return PluginSessionActivity.None;
            }

            switch (typeElement.GetString())
            {
                case "user":
                    return PluginSessionActivity.Busy;

                case "assistant":
                    var stopReason = root.TryGetProperty("message", out var message)
                        && message.ValueKind == System.Text.Json.JsonValueKind.Object
                        && message.TryGetProperty("stop_reason", out var reason)
                        && reason.ValueKind == System.Text.Json.JsonValueKind.String
                        ? reason.GetString()
                        : null;
                    return stopReason is "end_turn" or "stop_sequence" or "max_tokens"
                        ? PluginSessionActivity.TurnComplete
                        : PluginSessionActivity.Busy;

                default:
                    return PluginSessionActivity.None;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return PluginSessionActivity.None;
        }
    }

    /// <summary>The config directory this profile's transcripts live under, from the plugin's own config JSON — a pinned dir, else CLAUDE_CONFIG_DIR, else ~/.claude.</summary>
    private static string _ResolveStateDirectory(string configJson) =>
        ClaudeConfigPaths.ResolveStateDirectory(
            ClaudeProviderConfig.Parse(configJson).ConfigDir,
            Environment.GetEnvironmentVariable(ClaudeConfigPaths.EnvironmentVariable),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// Polls for a transcript file that was not present at launch — the one <c>claude</c> creates for this
    /// session under its own auto-assigned id. The newest such file wins if more than one appears (a rare
    /// race in the single-user cockpit). Polls rather than failing on a first miss: the CLI writes the file
    /// a moment after the pty is up.
    /// </summary>
    private static async Task<string?> _WaitForNewTranscriptAsync(
        string configDir, IReadOnlySet<string> knownTranscriptsAtLaunch, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var match = EnumerateTranscripts(configDir)
                .Where(path => !knownTranscriptsAtLaunch.Contains(path))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// Every <c>&lt;config-dir&gt;/projects/&lt;cwd-hash&gt;/&lt;id&gt;.jsonl</c> transcript currently on disk (session-id subfolders
    /// holding tool-results/subagents are skipped — only the flat transcript files count). Internal so
    /// <see cref="ClaudeTtyProvider"/> shares the one definition of what counts as a transcript; the policy for
    /// picking one out of several is deliberately not shared, because the two callers need opposite answers.
    /// </summary>
    internal static IEnumerable<string> EnumerateTranscripts(string configDir)
    {
        var projectsDir = Path.Combine(configDir, "projects");
        if (!Directory.Exists(projectsDir))
        {
            return [];
        }

        return Directory.EnumerateDirectories(projectsDir)
            .SelectMany(projectDir => Directory.EnumerateFiles(projectDir, "*.jsonl"));
    }
}
