using System.Runtime.CompilerServices;
using System.Text;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// <see cref="ISessionTranscriptReader"/>: polls for the session's transcript file to appear, then tails
/// it from its current end via manual byte-level buffering rather than <see cref="StreamReader.ReadLine"/>
/// — a plain line-reader can't tell a real end-of-file apart from "more is coming", so it would emit a
/// partial line the writer hasn't finished yet. A stateful <see cref="Decoder"/> carries any UTF-8
/// multi-byte sequence split across a poll boundary, so a wide character never gets corrupted either.
/// </summary>
internal sealed class ClaudeSessionTranscriptReader : ISessionTranscriptReader, ISingletonService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public async IAsyncEnumerable<string> ReadAssistantTextAsync(
        string configDir,
        Guid sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var transcriptPath = await _WaitForTranscriptFileAsync(configDir, sessionId, cancellationToken).ConfigureAwait(false);
        if (transcriptPath is null)
        {
            yield break;
        }

        await using var stream = new FileStream(
            transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        // Tail from the current end: whatever the session already wrote before this call is history,
        // not something to read aloud — only lines appended from here on are new turns.
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
                if (ClaudeTranscriptLineParser.TryExtractAssistantText(line, out var assistantText))
                {
                    yield return assistantText;
                }
            }

            pendingLine.Append(charBuffer, chunkStart, charCount - chunkStart);
        }
    }

    /// <summary>
    /// The transcript can appear a moment after the pty launch (the CLI creates it once its own
    /// session-id-tagged process is up), so this polls rather than failing on a first miss. The exact
    /// cwd-hash subfolder under <c>projects/</c> is not reproduced here — globbing for the session-id
    /// file name across every subfolder avoids depending on that hashing rule at all.
    /// </summary>
    private static async Task<string?> _WaitForTranscriptFileAsync(
        string configDir, Guid sessionId, CancellationToken cancellationToken)
    {
        var projectsDir = Path.Combine(configDir, "projects");
        var fileName = $"{sessionId}.jsonl";

        while (!cancellationToken.IsCancellationRequested)
        {
            if (Directory.Exists(projectsDir))
            {
                var match = Directory.EnumerateDirectories(projectsDir)
                    .Select(dir => Path.Combine(dir, fileName))
                    .FirstOrDefault(File.Exists);
                if (match is not null)
                {
                    return match;
                }
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }
}
