using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// Locates a TTY session's live JSONL transcript as the new <c>projects/*/​*.jsonl</c> file that appears
/// after launch (the session id is not forced — undocumented for interactive sessions — so the transcript
/// is singled out against a snapshot taken before spawn, not matched by name), then tails it from its
/// current end via manual byte-level buffering rather than <see cref="StreamReader.ReadLine"/> — a plain
/// line-reader can't tell a real end-of-file apart from "more is coming", so it would emit a partial line
/// the writer hasn't finished yet. A stateful <see cref="Decoder"/> carries any UTF-8 multi-byte sequence
/// split across a poll boundary, so a wide character never gets corrupted either.
/// </summary>
internal sealed class ClaudeSessionTranscriptReader(ILogger<ClaudeSessionTranscriptReader> logger)
    : ISessionTranscriptReader, ISingletonService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public IReadOnlySet<string> SnapshotTranscripts(string configDir) =>
        _EnumerateTranscripts(configDir).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async IAsyncEnumerable<string> ReadAssistantTextAsync(
        string configDir,
        IReadOnlySet<string> knownTranscriptsAtLaunch,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        logger.LogInformation("Read-aloud tailer started under {ConfigDir} ({KnownCount} transcripts known at launch)", configDir, knownTranscriptsAtLaunch.Count);
        await foreach (var line in ReadLinesAsync(configDir, knownTranscriptsAtLaunch, cancellationToken).ConfigureAwait(false))
        {
            if (ClaudeTranscriptLineParser.TryExtractAssistantText(line, out var assistantText))
            {
                logger.LogInformation("Read-aloud extracted {Length} chars of assistant text", assistantText.Length);
                yield return assistantText;
            }
        }
    }

    public async IAsyncEnumerable<string> ReadLinesAsync(
        string configDir,
        IReadOnlySet<string> knownTranscriptsAtLaunch,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
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

    /// <summary>
    /// Polls for a transcript file that was not present at launch — the one <c>claude</c> creates for this
    /// session under its own auto-assigned id. The newest such file wins if more than one appears (a rare
    /// race in the single-user cockpit). Polls rather than failing on a first miss: the CLI writes the file
    /// a moment after the pty is up.
    /// </summary>
    private async Task<string?> _WaitForNewTranscriptAsync(
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
                logger.LogInformation("Read-aloud found new transcript at {Path}", match);
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
