using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider;

// The Codex plugin's own transcript reader (AC-171) for the host's TTY status dot (#39) — a TTY session runs the
// real interactive TUI, so there is no parsed event stream, but `codex` writes every session live to
// `&lt;CODEX_HOME&gt;/sessions/&lt;yyyy&gt;/&lt;MM&gt;/&lt;dd&gt;/rollout-&lt;timestamp&gt;-&lt;id&gt;.jsonl`
// (confirmed against real `codex-cli 0.144.4` rollout files, not assumed from Claude's shape). Without this
// reader `CodexTtyProvider` registers no `CreateTranscriptReader`, so every Codex TTY session's
// status dot is set to `Idle` once at launch and never moves again — the bug AC-171 reports.
//
// Mirrors `ClaudeTranscriptReader`'s tail/poll/partial-line-buffer shape, minus the sub-agent handling: a
// Codex rollout carries no evidence of a sibling background-agent transcript, so `PluginSessionActivity.BackgroundBusy`
// is never emitted here — only `ReadActivityAsync`'s own two-state `task_started`/`task_complete`
// signal.
internal sealed class CodexTranscriptReader : IPluginTranscriptReader
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public IReadOnlySet<string> SnapshotTranscripts(string configJson) =>
        EnumerateTranscripts(_ResolveStateDirectory(configJson)).ToHashSet(StringComparer.OrdinalIgnoreCase);

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
        // Read from the start — unlike Claude, where the transcript file appears empty at launch and every
        // line is new, codex does not create the rollout file until the first turn actually starts: by the
        // time _WaitForNewTranscriptAsync's poll notices it, it can already contain both "session_meta" and
        // the first turn's "task_started" (confirmed against real rollouts: file birth time and that first
        // event's own timestamp match to the millisecond). Seeking to the end here would silently drop that
        // opening Busy signal for the common single-turn session — exactly AC-171's symptom. transcriptPath is
        // by construction not in knownTranscriptsAtLaunch, so nothing here predates this session; there is no
        // "history" to skip.
        var decoder = Encoding.UTF8.GetDecoder();
        var readBuffer = new byte[8192];
        var charBuffer = new char[readBuffer.Length];
        var pendingLine = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            var bytesRead = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead <= 0)
            {
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

                yield return new PluginTranscriptActivity(ClassifyLine(line), line);
            }

            pendingLine.Append(charBuffer, chunkStart, charCount - chunkStart);
        }
    }

    // Classifies one rollout-file JSONL line into a coarse turn-activity. A rollout line's top-level
    // `"type"` was observed as `"session_meta"`/`"turn_context"`/`"world_state"`/
    // `"response_item"`/`"event_msg"`/`"compacted"` across real transcripts — only
    // `"event_msg"` carries the turn-lifecycle signal this needs, in its own nested `payload.type`:
    // `"task_started"` opens a turn (Busy, confirmed to precede even the `"user_message"` event for
    // the same turn) and `"task_complete"` closes it (TurnComplete). Every other `payload.type`
    // (`agent_message`, `token_count`, `mcp_tool_call_end`, ...) and every non-`event_msg`
    // top-level type carries no signal — forward-compat with an unrecognized future value, same philosophy as
    // `CodexJsonlEventMapper`. Not exhaustively verified: a turn that ends by cancellation or crash rather
    // than a normal `task_complete` was not observed in the samples this reader was built against; the
    // host's own busy-safety-timeout (`TtyActivityStatusTracker`) is the backstop if that ever leaves a
    // session stuck on Busy.
    internal static PluginSessionActivity ClassifyLine(string? jsonLine)
    {
        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            return PluginSessionActivity.None;
        }

        try
        {
            using var document = JsonDocument.Parse(jsonLine);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String
                || typeElement.GetString() != "event_msg"
                || !root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("type", out var payloadType)
                || payloadType.ValueKind != JsonValueKind.String)
            {
                return PluginSessionActivity.None;
            }

            return payloadType.GetString() switch
            {
                "task_started" => PluginSessionActivity.Busy,
                "task_complete" => PluginSessionActivity.TurnComplete,
                _ => PluginSessionActivity.None,
            };
        }
        catch (JsonException)
        {
            return PluginSessionActivity.None;
        }
    }

    // The state directory this profile's rollouts live under, from the plugin's own config JSON — a pinned `CliAgentConfig.ConfigDir`, else `CODEX_HOME`, else `~/.codex` (the CLI's own default).
    private static string _ResolveStateDirectory(string configJson)
    {
        var config = _DeserializeConfig(configJson);
        if (!string.IsNullOrWhiteSpace(config.ConfigDir))
        {
            return config.ConfigDir;
        }

        var environmentConfigDir = Environment.GetEnvironmentVariable("CODEX_HOME");
        return string.IsNullOrWhiteSpace(environmentConfigDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : environmentConfigDir;
    }

    private static CliAgentConfig _DeserializeConfig(string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return new CliAgentConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<CliAgentConfig>(configJson, CliAgentConfig.JsonOptions) ?? new CliAgentConfig();
        }
        catch (JsonException)
        {
            return new CliAgentConfig();
        }
    }

    // Polls for a rollout file that was not present at launch — the one `codex` creates for this session
    // under its own auto-assigned id. The newest such file wins if more than one appears. Unlike Claude, whose
    // transcript appears within milliseconds of launch, `codex` does not create the rollout file until the
    // operator's first turn actually starts — the wait here can span the operator's entire idle time before
    // typing anything. Two Codex TTY sessions open at once therefore have a real window (not a rare race) in
    // which the first to start a turn "wins" a waiting reader on the other pane; this is a known, unresolved
    // limitation, not something this fix addresses.
    //
    // Resume (`codex resume &lt;id&gt;`/`--last`) is unverified: whether it appends to the existing
    // rollout file (in which case this wait never fires — it is already in `knownTranscriptsAtLaunch`
    // — and a resumed session's status never moves) or writes a fresh one was not confirmed against a real
    // resume run.
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

    // Every `&lt;configDir&gt;/sessions/yyyy/MM/dd/rollout-*.jsonl` transcript currently on disk — Codex
    // nests rollouts three directories deep by date, unlike Claude's flat `projects/&lt;hash&gt;/*.jsonl`,
    // so this recurses through the whole `sessions` tree rather than one directory level.
    internal static IEnumerable<string> EnumerateTranscripts(string configDir)
    {
        var sessionsDir = Path.Combine(configDir, "sessions");
        return Directory.Exists(sessionsDir)
            ? Directory.EnumerateFiles(sessionsDir, "rollout-*.jsonl", SearchOption.AllDirectories)
            : [];
    }
}
