using System.Diagnostics;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// Copies a download to disk while saying how far along it is. Both first-use fetches — the ~1.6 GB model and
/// a few hundred MB of GPU runtime — happen inside the first dictation, so the only alternative to narrating
/// them is a spinner that claims to be transcribing for several minutes.
/// </summary>
internal static class VoiceDownloadReporter
{
    /// <summary>Big enough that a fast link does not spend its time raising events, short enough to look live.</summary>
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(250);

    private const int BufferSize = 81920;

    /// <summary>
    /// Streams <paramref name="source"/> into <paramref name="target"/>, reporting progress as it goes.
    /// </summary>
    /// <param name="what">What is being fetched, e.g. "Downloading speech model" — used verbatim in the text.</param>
    /// <param name="totalBytes">
    /// The expected size when it is known (a Content-Length), so progress can be a fraction. Null reports
    /// megabytes instead: the ggml downloader hands out a stream with no length, and a bar that guesses its
    /// own position tells the operator something we do not know.
    /// </param>
    public static async Task CopyAsync(
        Stream source,
        Stream target,
        string what,
        long? totalBytes,
        IProgress<VoicePreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (progress is null)
        {
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);

            return;
        }

        var buffer = new byte[BufferSize];
        var copied = 0L;
        var sinceLastReport = Stopwatch.StartNew();

        progress.Report(_Describe(what, copied, totalBytes));

        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;

            // Throttled: a 1.6 GB model moves in 80 KB reads, and reporting each one would put twenty thousand
            // updates through the dispatcher to animate a number that changes faster than anyone can read it.
            if (sinceLastReport.Elapsed >= ReportInterval)
            {
                progress.Report(_Describe(what, copied, totalBytes));
                sinceLastReport.Restart();
            }
        }

        progress.Report(_Describe(what, copied, totalBytes));
    }

    private static VoicePreparationProgress _Describe(string what, long copied, long? totalBytes)
    {
        if (totalBytes is not > 0)
        {
            return new VoicePreparationProgress($"{what} — {_Megabytes(copied)} MB");
        }

        var fraction = Math.Clamp((double)copied / totalBytes.Value, 0, 1);

        return new VoicePreparationProgress(
            $"{what} — {fraction * 100:F0}% of {_Megabytes(totalBytes.Value)} MB", fraction);
    }

    private static long _Megabytes(long bytes) => bytes / 1024 / 1024;
}
