using SharpCompress.Common;
using SharpCompress.Readers;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// Resolves the on-disk cache for the SupertonicTTS model and downloads+extracts it on first use — mirrors
/// <see cref="WhisperModelCache"/> (never bundled/committed; the archive is tens of MB). Unlike the old
/// per-Piper-voice archives, Supertonic is one multilingual, multi-speaker model shared by every read-aloud
/// language: a single <c>.tar.bz2</c> that extracts to a folder of the same name holding the four int8 ONNX
/// graphs plus <c>tts.json</c>, <c>unicode_indexer.bin</c> and <c>voice.bin</c> (verified against the real
/// sherpa-onnx release asset). The weights are OpenRAIL-M licensed (commercial use allowed with attribution).
/// </summary>
internal static class SupertonicModelCache
{
    private const string ReleaseBaseUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models";

    /// <summary>Release archive name (without the <c>.tar.bz2</c> suffix); it also names the folder it extracts into.</summary>
    private const string ModelArchiveName = "sherpa-onnx-supertonic-3-tts-int8-2026-05-11";

    // One shared instance for the process lifetime, same rationale as the DI-registered HttpClient
    // singleton elsewhere in this project (Discord webhook): avoids per-download socket exhaustion.
    // This class is a static helper (mirroring WhisperModelCache), not itself DI-registered.
    private static readonly HttpClient HttpClient = new();

    private static string ModelsDirectory => Path.Combine(
        Path.GetDirectoryName(CockpitConfigPath.Default) ?? Path.GetTempPath(), "models", "supertonic");

    public static async Task<SupertonicModelPaths> EnsureDownloadedAsync(CancellationToken cancellationToken)
    {
        var modelDirectory = Path.Combine(ModelsDirectory, ModelArchiveName);
        var paths = _PathsFor(modelDirectory);
        if (_IsComplete(paths))
        {
            return paths;
        }

        Directory.CreateDirectory(ModelsDirectory);

        // Extract into a temp directory first so a cancelled/failed download-and-extract never leaves
        // a partial model behind that a later run would mistake for a complete, cached one.
        var tempDirectory = modelDirectory + ".download";
        if (Directory.Exists(tempDirectory))
        {
            Directory.Delete(tempDirectory, recursive: true);
        }

        Directory.CreateDirectory(tempDirectory);

        // Download the archive to a seekable temp file before extracting. SharpCompress's format
        // auto-detection rewinds the stream to sniff the header; a raw HttpClient response stream is
        // forward-only, so sniffing past its small rewind buffer throws ("Recording buffer overflow").
        // A file stream is seekable, so the reader can rewind freely.
        var archivePath = Path.Combine(tempDirectory, $"{ModelArchiveName}.tar.bz2");
        await using (var httpStream = await HttpClient
                         .GetStreamAsync($"{ReleaseBaseUrl}/{ModelArchiveName}.tar.bz2", cancellationToken)
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

        // The archive extracts into its own top-level "{ModelArchiveName}/" folder — flatten that up one
        // level so modelDirectory holds the ONNX graphs and support files directly.
        var extractedRoot = Path.Combine(tempDirectory, ModelArchiveName);
        if (Directory.Exists(modelDirectory))
        {
            Directory.Delete(modelDirectory, recursive: true);
        }

        Directory.Move(extractedRoot, modelDirectory);
        Directory.Delete(tempDirectory, recursive: true);

        return paths;
    }

    private static SupertonicModelPaths _PathsFor(string modelDirectory) => new(
        DurationPredictorPath: Path.Combine(modelDirectory, "duration_predictor.int8.onnx"),
        TextEncoderPath: Path.Combine(modelDirectory, "text_encoder.int8.onnx"),
        VectorEstimatorPath: Path.Combine(modelDirectory, "vector_estimator.int8.onnx"),
        VocoderPath: Path.Combine(modelDirectory, "vocoder.int8.onnx"),
        TtsJsonPath: Path.Combine(modelDirectory, "tts.json"),
        UnicodeIndexerPath: Path.Combine(modelDirectory, "unicode_indexer.bin"),
        VoiceStylePath: Path.Combine(modelDirectory, "voice.bin"));

    private static bool _IsComplete(SupertonicModelPaths paths) =>
        File.Exists(paths.DurationPredictorPath)
        && File.Exists(paths.TextEncoderPath)
        && File.Exists(paths.VectorEstimatorPath)
        && File.Exists(paths.VocoderPath)
        && File.Exists(paths.TtsJsonPath)
        && File.Exists(paths.UnicodeIndexerPath)
        && File.Exists(paths.VoiceStylePath);
}
