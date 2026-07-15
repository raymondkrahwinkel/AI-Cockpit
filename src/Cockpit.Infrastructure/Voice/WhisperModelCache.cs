using Microsoft.Extensions.Logging;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Configuration;
using Whisper.net.Ggml;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// Resolves the on-disk cache path for a ggml model and downloads it on first use via
/// <see cref="WhisperGgmlDownloader"/> — never bundled/committed to the repo (a ~1.6 GB
/// <c>large-v3-turbo</c> download would otherwise hit every clone). Lives next to <c>cockpit.json</c>
/// under the user's app-data directory, one file per model so switching models does not re-download.
/// </summary>
internal static class WhisperModelCache
{
    private static string ModelsDirectory => Path.Combine(
        Path.GetDirectoryName(CockpitConfigPath.Default) ?? Path.GetTempPath(), "models");

    public static async Task<string> EnsureDownloadedAsync(
        GgmlType type,
        CancellationToken cancellationToken,
        ILogger? logger = null,
        IProgress<VoicePreparationProgress>? progress = null)
    {
        Directory.CreateDirectory(ModelsDirectory);
        var path = Path.Combine(ModelsDirectory, $"ggml-{type.ToString().ToLowerInvariant()}.bin");
        if (File.Exists(path))
        {
            return path;
        }

        // First use on this machine: the model is fetched now (large-v3-turbo is ~1.6 GB). This can take
        // minutes and the whole dictation pipeline blocks on it — logged loudly, and reported through
        // progress so the operator sees the download rather than a spinner claiming to transcribe.
        logger?.LogInformation("Whisper model '{Model}' is not cached yet; downloading it now (first use — this can take several minutes)", type);
        await using var modelStream = await WhisperGgmlDownloader.Default
            .GetGgmlModelAsync(type, QuantizationType.NoQuantization, cancellationToken)
            .ConfigureAwait(false);

        // Download to a temp file first so a cancelled/failed download never leaves a truncated file
        // behind that a later run would mistake for a complete, cached model.
        var tempPath = path + ".download";
        await using (var fileStream = File.Create(tempPath))
        {
            // No length: the ggml downloader hands back a bare stream, so this one counts megabytes rather
            // than inventing a percentage.
            await VoiceDownloadReporter
                .CopyAsync(modelStream, fileStream, "Downloading speech model", totalBytes: null, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(tempPath, path, overwrite: true);
        logger?.LogInformation("Whisper model '{Model}' downloaded and cached at {Path}", type, path);
        return path;
    }

    /// <summary>Same lazy-download-and-cache behaviour as <see cref="EnsureDownloadedAsync"/>, for the ggml Silero VAD model.</summary>
    public static async Task<string> EnsureVadDownloadedAsync(SileroVadType type, CancellationToken cancellationToken, ILogger? logger = null)
    {
        Directory.CreateDirectory(ModelsDirectory);
        var path = Path.Combine(ModelsDirectory, $"ggml-silero-{type.ToString().ToLowerInvariant()}.bin");
        if (File.Exists(path))
        {
            return path;
        }

        logger?.LogInformation("Silero VAD model '{Model}' is not cached yet; downloading it now (first use)", type);
        await using var modelStream = await WhisperGgmlDownloader.Default
            .GetGgmlSileroVadModelAsync(type, cancellationToken)
            .ConfigureAwait(false);

        var tempPath = path + ".download";
        await using (var fileStream = File.Create(tempPath))
        {
            await modelStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, path, overwrite: true);
        logger?.LogInformation("Silero VAD model '{Model}' downloaded and cached", type);
        return path;
    }
}
