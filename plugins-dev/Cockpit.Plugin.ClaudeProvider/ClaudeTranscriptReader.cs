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

    public async IAsyncEnumerable<string> ReadAssistantTextAsync(
        string configJson,
        IReadOnlySet<string> knownTranscriptsAtLaunch,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in ReadLinesAsync(configJson, knownTranscriptsAtLaunch, cancellationToken).ConfigureAwait(false))
        {
            if (ClaudeTranscriptLineParser.TryExtractAssistantText(line, out var assistantText))
            {
                yield return assistantText;
            }
        }
    }

    public async IAsyncEnumerable<string> ReadLinesAsync(
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
                yield return line;
            }

            pendingLine.Append(charBuffer, chunkStart, charCount - chunkStart);
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
