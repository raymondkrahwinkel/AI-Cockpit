using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

// The Claude plugin's own transcript reader (weg A) for the host's status (#39): a TTY session runs the real
// interactive TUI, so there is no parsed event stream — but `claude` writes every session live to
// `&lt;config-dir&gt;/projects/&lt;cwd-hash&gt;/&lt;session-id&gt;.jsonl`, so tailing that file gets the
// turn's activity cleanly without touching the ANSI/TUI stream. Ported from the host's former in-tree reader
// so the core carries no Claude-format knowledge; the config directory is resolved from this plugin's own
// opaque `ConfigJson` rather than a host-supplied path.
//
// The session id is *not* forced on the launch (undocumented for interactive sessions and does not
// persist a transcript), so the file is identified as the new transcript that appears after launch — see
// `SnapshotTranscripts`. It is tailed from its current end via manual byte-level buffering rather
// than `StreamReader.ReadLine`, which cannot tell a real end-of-file apart from "more is coming"
// and would emit a partial line the writer has not finished; a stateful `Decoder` carries any
// UTF-8 multi-byte sequence split across a poll boundary.
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

    public IAsyncEnumerable<PluginTranscriptActivity> ReadActivityAsync(
        string configJson,
        IReadOnlySet<string> knownTranscriptsAtLaunch,
        CancellationToken cancellationToken) =>
        ReadActivityAsync(configJson, knownTranscriptsAtLaunch, statusFile: null, cancellationToken);

    // Tails this session's transcript (AC-609): the file `claude` names in its own statusline snapshot when
    // the host has one, and only otherwise the pre-launch-snapshot guess the older overload is stuck with.
    //
    // The tail is re-resolved whenever the file stops producing, so a session that starts a fresh conversation
    // mid-pane (`/clear` mints a new session id, and with it a new transcript) is followed across the change
    // instead of sitting forever on the end of the file it has stopped writing to.
    public async IAsyncEnumerable<PluginTranscriptActivity> ReadActivityAsync(
        string configJson,
        IReadOnlySet<string> knownTranscriptsAtLaunch,
        string? statusFile,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var configDir = _ResolveStateDirectory(configJson);
        var first = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            var transcriptPath = await _WaitForTranscriptAsync(configDir, knownTranscriptsAtLaunch, statusFile, cancellationToken).ConfigureAwait(false);
            if (transcriptPath is null)
            {
                yield break;
            }

            // The first file is read from its end — a resumed session continues the record it had, and replaying an
            // afternoon of history as fresh activity would be worse than the silence it replaces. A file we moved
            // to is a conversation that started after we were already watching, so it is read whole: it is short by
            // definition, and its opening lines are the ones that say a new turn is under way.
            await foreach (var reading in _TailAsync(transcriptPath, statusFile, fromStart: !first, cancellationToken).ConfigureAwait(false))
            {
                yield return reading;
            }

            first = false;
        }
    }

    // Reads one transcript file to its end, giving up only when the session moved to another one — see
    // `_MovedOn`. Split out of `ReadActivityAsync(string, IReadOnlySet{string}, string?, CancellationToken)`
    // so the re-resolve loop above reads as the one sentence it is.
    private static async IAsyncEnumerable<PluginTranscriptActivity> _TailAsync(
        string transcriptPath,
        string? statusFile,
        bool fromStart,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        // Tail from the current end: whatever the session already wrote before this call is history,
        // not new activity — only lines appended from here on are new turns. Unless this is a record the session
        // moved to while we were watching, whose whole content is new activity by construction.
        stream.Seek(0, fromStart ? SeekOrigin.Begin : SeekOrigin.End);

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

                // Only ever checked on a quiet poll, and only against the file the session names for itself: a
                // transcript that is still producing is this session's by definition, and re-reading the snapshot
                // on every chunk would put a file read in the hot path for a question that changes about once a day.
                if (_MovedOn(statusFile, transcriptPath))
                {
                    yield break;
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

    // Reads back what this session has already written (AC-609) — the tail of the very file
    // `ReadActivityAsync(string, IReadOnlySet{string}, string?, CancellationToken)` follows, named by
    // the session itself. A TTY session hosts the real TUI, so this file is the only record of it that exists;
    // without it the cockpit's read surfaces have nothing to answer with but a guess about a pane they can see is
    // alive.
    //
    // Held to `count` rows as it reads rather than after: a session that has been running all day
    // writes a transcript in the tens of megabytes, and materialising it whole to keep the last thirty rows is a
    // cost nobody would notice until the day it matters. The whole file is still walked — the total is the thing a
    // caller needs in order not to report a tail as a conversation.
    public PluginTranscriptSlice ReadEntries(string? statusFile, int count)
    {
        if (count <= 0 || TranscriptPathFrom(statusFile) is not { } transcriptPath)
        {
            return PluginTranscriptSlice.Empty;
        }

        try
        {
            var kept = new List<_Row>();
            var total = 0;
            // ReadWrite share: the CLI has this file open and is appending to it right now.
            using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                foreach (var row in _RowsFrom(line, kept))
                {
                    total++;
                    kept.Add(row);
                    if (kept.Count > count)
                    {
                        kept.RemoveAt(0);
                    }
                }
            }

            return new PluginTranscriptSlice(
                [.. kept.Select(row => new PluginTranscriptEntry(row.Kind, row.Text, row.ToolResult))], total);
        }
        catch (Exception)
        {
            // A file caught mid-write, a session that just ended, a transcript that has been rotated away. An empty
            // answer is honest; throwing here would take out a read surface for a session that is working fine.
            return PluginTranscriptSlice.Empty;
        }
    }

    // <summary>One row being assembled: a record would do, except a tool call learns its result a line later.</summary>
    private sealed class _Row
    {
        public required PluginTranscriptEntryKind Kind { get; init; }

        public required string Text { get; init; }

        public string? ToolResult { get; set; }

        // <summary>The call id, on a `PluginTranscriptEntryKind.ToolUse` row, so its result finds it.</summary>
        public string? ToolUseId { get; init; }
    }

    // The rows one transcript line contributes — none for the CLI's own bookkeeping lines (mode changes, titles,
    // file-history deltas), and more than one for a message whose content holds several blocks, which is the
    // ordinary shape of a turn: some prose, a thinking block, three tool calls.
    //
    // A tool result is folded onto the call it belongs to rather than shown as a row of its own, matching how the
    // SDK path reports one. It is looked up in `pending`, the rows still in hand: results follow
    // their call immediately, so the only ones that miss are calls made before the slice began — and those become
    // a row of their own rather than being dropped, since a result with no visible call still says what happened.
    private static IEnumerable<_Row> _RowsFrom(string line, List<_Row> pending)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            var root = document.RootElement;
            var role = _Text(root, "type");
            if (role is not ("user" or "assistant")
                || !root.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content))
            {
                yield break;
            }

            var isUser = role is "user";
            if (content.ValueKind == JsonValueKind.String)
            {
                // The plain-string shape the CLI writes for a typed prompt.
                var text = content.GetString() ?? string.Empty;
                if (text.Length > 0)
                {
                    yield return new _Row { Kind = PluginTranscriptEntryKind.UserText, Text = text };
                }

                yield break;
            }

            if (content.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object
                    || !block.TryGetProperty("type", out var blockType))
                {
                    continue;
                }

                switch (blockType.GetString())
                {
                    case "text" when _Text(block, "text") is { Length: > 0 } text:
                        yield return new _Row
                        {
                            Kind = isUser ? PluginTranscriptEntryKind.UserText : PluginTranscriptEntryKind.AssistantText,
                            Text = text,
                        };
                        break;

                    // A redacted thinking block carries a signature and no text — nothing to show, but the row is
                    // still what happened, so it is reported as an empty thinking rather than left out.
                    case "thinking":
                        yield return new _Row
                        {
                            Kind = PluginTranscriptEntryKind.Thinking,
                            Text = _Text(block, "thinking") ?? string.Empty,
                        };
                        break;

                    case "tool_use":
                        yield return new _Row
                        {
                            Kind = PluginTranscriptEntryKind.ToolUse,
                            Text = _ToolCall(block),
                            ToolUseId = _Text(block, "id"),
                        };
                        break;

                    case "tool_result":
                        var id = _Text(block, "tool_use_id");
                        var result = _ResultText(block);
                        var call = id is null
                            ? null
                            : pending.LastOrDefault(row => row.ToolUseId == id);
                        if (call is not null)
                        {
                            call.ToolResult = result;
                        }
                        else
                        {
                            yield return new _Row { Kind = PluginTranscriptEntryKind.ToolResult, Text = result };
                        }

                        break;
                }
            }
        }
    }

    // <summary>A tool call as one line: its name, and its arguments as the JSON they were sent as.</summary>
    private static string _ToolCall(JsonElement block)
    {
        var name = _Text(block, "name") ?? "tool";
        return block.TryGetProperty("input", out var input) && input.ValueKind != JsonValueKind.Undefined
            ? $"{name} {input.GetRawText()}"
            : name;
    }

    // What a tool returned, as text. The CLI writes it as a plain string or as a list of blocks; the blocks that
    // carry text are joined, and anything else is handed over as the JSON it is rather than silently dropped —
    // the caller reading this is trying to work out what a session did, and a blank is the one answer that helps
    // with nothing.
    private static string _ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return content.GetRawText();
        }

        return string.Join(
            "\n",
            content.EnumerateArray().Select(part => _Text(part, "text") ?? part.GetRawText()));
    }

    private static string? _Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    // Classifies one main-transcript JSONL line into a coarse turn-activity: a user message or tool-result means
    // the model owes a response (Busy); an assistant message is AwaitingOperator on an `AskUserQuestion` tool
    // call, Busy while it otherwise streams or loops, TurnComplete on a terminal stop_reason; anything else None.
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
                    if (_AsksTheOperatorAQuestion(message))
                    {
                        return PluginSessionActivity.AwaitingOperator;
                    }

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

    // AC-920: `AskUserQuestion` is the CLI's own interactive prompt — it has no non-interactive path, so a
    // `tool_use` block naming it always means a human, not the model, has to answer next.
    private static bool _AsksTheOperatorAQuestion(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object
        && message.TryGetProperty("content", out var content)
        && content.ValueKind == JsonValueKind.Array
        && content.EnumerateArray().Any(block =>
            _Text(block, "type") == "tool_use" && _Text(block, "name") == "AskUserQuestion");

    // <summary>The config directory this profile's transcripts live under, from the plugin's own config JSON — a pinned dir, else CLAUDE_CONFIG_DIR, else ~/.claude.</summary>
    private static string _ResolveStateDirectory(string configJson) =>
        ClaudeConfigPaths.ResolveStateDirectory(
            ClaudeProviderConfig.Parse(configJson).ConfigDir,
            Environment.GetEnvironmentVariable(ClaudeConfigPaths.EnvironmentVariable),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    // How long a session that has a statusline snapshot is given to write one before the guess below is used
    // anyway. The CLI renders its statusline as it comes up, so this is not a wait anyone sees — it is the bound
    // on how long a *broken* relay (a settings merge that failed, a script the shell refused to run) costs
    // before the session falls back to the old behaviour rather than silently getting no status at all.
    private static readonly TimeSpan StatusFilePatience = TimeSpan.FromSeconds(30);

    // This session's transcript: the path `claude` states in its own statusline snapshot, and only failing
    // that the newest file that was not on disk at launch.
    //
    // **Why the snapshot and not the guess (AC-609).** The guess is a race the session frequently loses, and
    // losing it is silent and permanent. Every other invocation of the CLI on the machine writes a new transcript
    // too — another pane, an SDK session, a delegated task, a `-p` one-shot, the operator's own terminal —
    // and several of those are short-lived files that appear, are written once and are never touched again. A
    // launch that latches onto one of those tails a file that has stopped growing: no line ever arrives, so the
    // host is handed no activity at all, and its tracker reports the "before any signal" status — Idle — for the
    // entire life of a session that is working normally. Measured on the reported case, the foreign file was
    // created 18 seconds before the session's own. There is no window that fixes this, because the reader is
    // guessing at an identity the CLI is willing to state outright.
    //
    // The snapshot's `transcript_path` is that statement. It is also re-read while tailing, so a
    // `/clear` — which mints a new session id and a new file — is followed rather than lost.
    private static async Task<string?> _WaitForTranscriptAsync(
        string configDir, IReadOnlySet<string> knownTranscriptsAtLaunch, string? statusFile, CancellationToken cancellationToken)
    {
        var waitedForStatusFile = TimeSpan.Zero;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (TranscriptPathFrom(statusFile) is { } stated)
            {
                return stated;
            }

            // Not raced against while there is still a statusline to wait for: falling back early is how the
            // wrong file gets picked, and the whole point here is to stop doing that.
            if (statusFile is null || waitedForStatusFile >= StatusFilePatience)
            {
                var match = EnumerateTranscripts(configDir)
                    .Where(path => !knownTranscriptsAtLaunch.Contains(path))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (match is not null)
                {
                    return match;
                }
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            waitedForStatusFile += PollInterval;
        }

        return null;
    }

    // Whether the session has moved to a different transcript than the one being tailed — a `/clear`, or a
    // resume that forked a new session id. Answers false when there is nothing to compare against, so a session
    // without a statusline snapshot behaves exactly as it did before.
    private static bool _MovedOn(string? statusFile, string tailing) =>
        TranscriptPathFrom(statusFile) is { } stated && !string.Equals(stated, tailing, StringComparison.Ordinal);

    // The transcript the CLI names in a statusline snapshot, or null when there is no snapshot yet, it cannot be
    // read (caught mid-rename — the next poll brings a whole one), it names nothing, or it names something that
    // is not on disk. Never throws: this is called from a poll loop whose failure mode is a session with no status.
    internal static string? TranscriptPathFrom(string? statusFile)
    {
        if (string.IsNullOrWhiteSpace(statusFile))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(statusFile));
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.TryGetProperty("transcript_path", out var path)
                && path.ValueKind == System.Text.Json.JsonValueKind.String
                && path.GetString() is { Length: > 0 } value
                && File.Exists(value)
                    ? value
                    : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Every `&lt;config-dir&gt;/projects/&lt;cwd-hash&gt;/&lt;id&gt;.jsonl` transcript currently on disk (session-id subfolders
    // holding tool-results/subagents are skipped — only the flat transcript files count). Internal so
    // `ClaudeTtyProvider` shares the one definition of what counts as a transcript; the policy for
    // picking one out of several is deliberately not shared, because the two callers need opposite answers.
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
