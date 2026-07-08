using SharpCompress.Common;
using SharpCompress.Readers;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// Resolves the on-disk cache for a Piper voice and downloads+extracts it on first use — mirrors
/// <see cref="WhisperModelCache"/> (never bundled/committed; a single archive can be tens of MB). Each
/// sherpa-onnx Piper release archive is named <c>vits-piper-{voiceId}.tar.bz2</c> and extracts to a
/// folder of the same name containing <c>{voiceId}.onnx</c>, <c>tokens.txt</c> and an
/// <c>espeak-ng-data/</c> phonemization data directory (verified against the real release asset,
/// 2026-07-08).
/// </summary>
internal static class PiperVoiceCache
{
    private const string ReleaseBaseUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models";

    // One shared instance for the process lifetime, same rationale as the DI-registered HttpClient
    // singleton elsewhere in this project (Discord webhook): avoids per-download socket exhaustion.
    // This class is a static helper (mirroring WhisperModelCache), not itself DI-registered.
    private static readonly HttpClient HttpClient = new();

    private static string VoicesDirectory => Path.Combine(
        Path.GetDirectoryName(CockpitConfigPath.Default) ?? Path.GetTempPath(), "models", "piper-voices");

    public static async Task<PiperVoicePaths> EnsureDownloadedAsync(string voiceId, CancellationToken cancellationToken)
    {
        var voiceDirectory = Path.Combine(VoicesDirectory, voiceId);
        var paths = _PathsFor(voiceDirectory, voiceId);
        if (_IsComplete(paths))
        {
            return paths;
        }

        Directory.CreateDirectory(VoicesDirectory);

        // Extract into a temp directory first so a cancelled/failed download-and-extract never leaves
        // a partial voice behind that a later run would mistake for a complete, cached one.
        var tempDirectory = voiceDirectory + ".download";
        if (Directory.Exists(tempDirectory))
        {
            Directory.Delete(tempDirectory, recursive: true);
        }

        Directory.CreateDirectory(tempDirectory);

        var archiveName = $"vits-piper-{voiceId}";

        // Download the archive to a seekable temp file before extracting. SharpCompress's format
        // auto-detection rewinds the stream to sniff the header; a raw HttpClient response stream is
        // forward-only, so sniffing past its small rewind buffer throws ("Recording buffer overflow").
        // A file stream is seekable, so the reader can rewind freely.
        var archivePath = Path.Combine(tempDirectory, $"{archiveName}.tar.bz2");
        await using (var httpStream = await HttpClient
                         .GetStreamAsync($"{ReleaseBaseUrl}/{archiveName}.tar.bz2", cancellationToken)
                         .ConfigureAwait(false))
        await using (var fileStream = File.Create(archivePath))
        {
            await httpStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
        }

        await using (var archiveStream = File.OpenRead(archivePath))
        using (var reader = ReaderFactory.OpenReader(archiveStream, new ReaderOptions()))
        {
            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.Entry.IsDirectory)
                {
                    continue;
                }

                reader.WriteEntryToDirectory(tempDirectory, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
            }
        }

        File.Delete(archivePath);

        // The archive extracts into its own top-level "vits-piper-{voiceId}/" folder — flatten that up
        // one level so voiceDirectory holds the model/tokens/data-dir directly.
        var extractedRoot = Path.Combine(tempDirectory, archiveName);
        if (Directory.Exists(voiceDirectory))
        {
            Directory.Delete(voiceDirectory, recursive: true);
        }

        Directory.Move(extractedRoot, voiceDirectory);
        Directory.Delete(tempDirectory, recursive: true);

        return paths;
    }

    private static PiperVoicePaths _PathsFor(string voiceDirectory, string voiceId) => new(
        ModelPath: Path.Combine(voiceDirectory, $"{voiceId}.onnx"),
        TokensPath: Path.Combine(voiceDirectory, "tokens.txt"),
        DataDirectoryPath: Path.Combine(voiceDirectory, "espeak-ng-data"));

    private static bool _IsComplete(PiperVoicePaths paths) =>
        File.Exists(paths.ModelPath) && File.Exists(paths.TokensPath) && Directory.Exists(paths.DataDirectoryPath);
}
