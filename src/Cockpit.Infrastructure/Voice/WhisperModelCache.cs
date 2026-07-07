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

    public static async Task<string> EnsureDownloadedAsync(GgmlType type, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ModelsDirectory);
        var path = Path.Combine(ModelsDirectory, $"ggml-{type.ToString().ToLowerInvariant()}.bin");
        if (File.Exists(path))
        {
            return path;
        }

        await using var modelStream = await WhisperGgmlDownloader.Default
            .GetGgmlModelAsync(type, QuantizationType.NoQuantization, cancellationToken)
            .ConfigureAwait(false);

        // Download to a temp file first so a cancelled/failed download never leaves a truncated file
        // behind that a later run would mistake for a complete, cached model.
        var tempPath = path + ".download";
        await using (var fileStream = File.Create(tempPath))
        {
            await modelStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, path, overwrite: true);
        return path;
    }

    /// <summary>Same lazy-download-and-cache behaviour as <see cref="EnsureDownloadedAsync"/>, for the ggml Silero VAD model.</summary>
    public static async Task<string> EnsureVadDownloadedAsync(SileroVadType type, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ModelsDirectory);
        var path = Path.Combine(ModelsDirectory, $"ggml-silero-{type.ToString().ToLowerInvariant()}.bin");
        if (File.Exists(path))
        {
            return path;
        }

        await using var modelStream = await WhisperGgmlDownloader.Default
            .GetGgmlSileroVadModelAsync(type, cancellationToken)
            .ConfigureAwait(false);

        var tempPath = path + ".download";
        await using (var fileStream = File.Create(tempPath))
        {
            await modelStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, path, overwrite: true);
        return path;
    }
}
