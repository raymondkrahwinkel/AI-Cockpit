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
        _EnumerateTranscripts(_ResolveStateDirectory(configJson)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How recently a sub-agent transcript must have been written to still count as running. Generous on purpose:
    /// a sub-agent that pauses to think writes nothing for a while yet is still working, so a short window would
    /// wrongly declare it finished. A <em>stopped</em> agent does not rely on this window lapsing — the main
    /// transcript's <c>agents_killed</c> system line ends it at once — and a completed one ends when the main agent
    /// resumes on its result, so this only has to outlast a sub-agent's own thinking pauses.
    /// </summary>
    private static readonly TimeSpan SubAgentActivityWindow = TimeSpan.FromSeconds(30);

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

        // Background sub-agents (the "Agent" tool) are recorded in a sibling directory named after the session id,
        // not in the main transcript — <dir>/<id>.jsonl (tailed here) alongside <dir>/<id>/subagents/*.jsonl. A
        // backgrounded agent even ends the main agent's own turn, so the session is not done while one still runs.
        var subAgentDir = Path.Combine(
            Path.GetDirectoryName(transcriptPath) ?? configDir,
            Path.GetFileNameWithoutExtension(transcriptPath),
            "subagents");

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
        // The main transcript authoritatively brackets a background agent's life: an "Agent" tool_use starts one,
        // a system "agents_killed" line ends every running one at once (a stopped agent's own transcript has no
        // clean end, so the folder mtime alone would linger). Recent sub-agent writes then say it is still going.
        var agentsKilled = false;
        var lastEmitted = PluginSessionActivity.None;

        bool SubAgentsRunning() => !agentsKilled && _SubAgentsActive(subAgentDir);

        while (!cancellationToken.IsCancellationRequested)
        {
            var bytesRead = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead <= 0)
            {
                if (SubAgentsRunning())
                {
                    // The main agent is quiet but a background agent is still writing — keep the session off "done"
                    // and shown as background work, re-emitted each poll so the host's safety timeout never fires.
                    lastEmitted = PluginSessionActivity.BackgroundBusy;
                    yield return new PluginTranscriptActivity(PluginSessionActivity.BackgroundBusy, null);
                }
                else if (lastEmitted == PluginSessionActivity.BackgroundBusy)
                {
                    // The background work just ended (the agent finished or was killed); move off "working" to the
                    // main agent's own state, so the dot does not stay stuck on background after the sub-agent is gone.
                    lastEmitted = mainTurnComplete ? PluginSessionActivity.TurnComplete : PluginSessionActivity.Busy;
                    yield return new PluginTranscriptActivity(lastEmitted, null);
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

                if (_IsAgentsKilled(line))
                {
                    agentsKilled = true;
                }
                else if (_IsAgentSpawn(line))
                {
                    agentsKilled = false;
                }

                var activity = ClassifyLine(line);
                if (activity == PluginSessionActivity.TurnComplete)
                {
                    mainTurnComplete = true;
                }
                else if (activity == PluginSessionActivity.Busy)
                {
                    mainTurnComplete = false;
                }

                // A completed main turn while a background agent still runs is background work, not done — the dot
                // should read "working (background)" until the agent itself ends.
                var emit = activity == PluginSessionActivity.TurnComplete && SubAgentsRunning()
                    ? PluginSessionActivity.BackgroundBusy
                    : activity;
                if (emit != PluginSessionActivity.None)
                {
                    lastEmitted = emit;
                }

                yield return new PluginTranscriptActivity(emit, line);
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

    /// <summary>True when a sub-agent transcript under <paramref name="subAgentDir"/> was written within <see cref="SubAgentActivityWindow"/> — the session is still doing background work.</summary>
    private static bool _SubAgentsActive(string subAgentDir)
    {
        if (!Directory.Exists(subAgentDir))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        try
        {
            return Directory.EnumerateFiles(subAgentDir, "*.jsonl")
                .Any(file => now - File.GetLastWriteTimeUtc(file) < SubAgentActivityWindow);
        }
        catch (Exception)
        {
            // A sub-agent file vanishing mid-enumeration is not a status error — treat as no background activity.
            return false;
        }
    }

    /// <summary>True for the main-transcript <c>system</c> line Claude writes when background agents are stopped (<c>subtype: "agents_killed"</c>) — ends the background-work state at once rather than waiting for the folder to go quiet.</summary>
    private static bool _IsAgentsKilled(string jsonLine) =>
        _MatchesSystemSubtype(jsonLine, "agents_killed");

    /// <summary>True when the line is an assistant message spawning a background agent (a <c>tool_use</c> named <c>"Agent"</c>) — clears a prior kill so a newly started agent counts again.</summary>
    private static bool _IsAgentSpawn(string jsonLine)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(jsonLine);
            var root = document.RootElement;
            if (!root.TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var content)
                || content.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return false;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var type) && type.GetString() == "tool_use"
                    && block.TryGetProperty("name", out var name) && name.GetString() == "Agent")
                {
                    return true;
                }
            }

            return false;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool _MatchesSystemSubtype(string jsonLine, string subtype)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(jsonLine);
            var root = document.RootElement;
            return root.TryGetProperty("type", out var type) && type.GetString() == "system"
                && root.TryGetProperty("subtype", out var sub) && sub.GetString() == subtype;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
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
            var match = _EnumerateTranscripts(configDir)
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

    /// <summary>Every <c>&lt;config-dir&gt;/projects/&lt;cwd-hash&gt;/&lt;id&gt;.jsonl</c> transcript currently on disk (session-id subfolders holding tool-results/subagents are skipped — only the flat transcript files count).</summary>
    private static IEnumerable<string> _EnumerateTranscripts(string configDir)
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
