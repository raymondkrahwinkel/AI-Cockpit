using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Cockpit.Core.Voice;
using Whisper.net;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// The protocol between the desktop process and a dictation transcription child (AC-174, Raymond 2026-07-22). Whisper.net
/// loads a native runtime that can <c>abort()</c> on a bad model/backend — a native crash no managed handler can catch,
/// which took the whole app down (a ggml_abort in whisper_model_load). So transcription runs in a child process, the same
/// way calibration already does: the desktop spawns this exe with <see cref="TranscribeArgument"/> plus the backend, model
/// and language, streams 16 kHz mono float32 clips to it over stdin, and reads text (and progress) back as prefixed lines
/// on stdout. If the child aborts, only the child dies; the desktop sees the pipe close and carries on.
/// <para>
/// Wire format: stdin is binary — each request is an Int32 little-endian sample count followed by that many little-endian
/// float32 samples; a count of 0 or stdin EOF asks the child to exit. stdout is the line protocol below.
/// </para>
/// </summary>
internal static class DictationWorkerProtocol
{
    public const string TranscribeArgument = "--transcribe-dictation";
    public const string BackendArgument = "--dictation-backend";
    public const string ModelArgument = "--dictation-model";
    public const string LanguageArgument = "--dictation-language";
    public const string LinePrefix = "DICTATE ";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Encode(DictationChildMessage message) => LinePrefix + JsonSerializer.Serialize(message, Json);

    public static DictationChildMessage? Decode(string line) =>
        line.StartsWith(LinePrefix, StringComparison.Ordinal)
            ? JsonSerializer.Deserialize<DictationChildMessage>(line[LinePrefix.Length..], Json)
            : null;

    /// <summary>Reads the backend/model/language out of the process arguments, or false when this is not a dictation child.</summary>
    public static bool TryReadRequest(string[] args, out VoiceBackendPreference backend, out string model, out string language)
    {
        backend = VoiceBackendPreference.Cpu;
        model = "large-v3-turbo";
        language = "auto";
        if (!args.Contains(TranscribeArgument))
        {
            return false;
        }

        var backendIndex = Array.IndexOf(args, BackendArgument);
        if (backendIndex >= 0 && backendIndex + 1 < args.Length)
        {
            Enum.TryParse(args[backendIndex + 1], out backend);
        }

        var modelIndex = Array.IndexOf(args, ModelArgument);
        if (modelIndex >= 0 && modelIndex + 1 < args.Length && !string.IsNullOrWhiteSpace(args[modelIndex + 1]))
        {
            model = args[modelIndex + 1];
        }

        var languageIndex = Array.IndexOf(args, LanguageArgument);
        if (languageIndex >= 0 && languageIndex + 1 < args.Length && !string.IsNullOrWhiteSpace(args[languageIndex + 1]))
        {
            language = args[languageIndex + 1];
        }

        return true;
    }
}

/// <summary>One line from a dictation child: a preparation step, a transcription result, a ready signal, or an error.</summary>
internal sealed record DictationChildMessage(string Kind, string? Message = null, double? Fraction = null, string? Text = null)
{
    public const string KindProgress = "progress";
    public const string KindReady = "ready";
    public const string KindResult = "result";
    public const string KindError = "error";
}

/// <summary>
/// The public seam <c>Program.Main</c> uses to run a dictation child (AC-174), mirroring <see cref="HeadlessCalibration"/>:
/// kept tiny and public so Main can branch into headless transcription before the single-instance guard, Avalonia, or DI —
/// none of which a transcription worker should pay for or contend with.
/// </summary>
public static class HeadlessDictation
{
    /// <summary>Whether these process arguments ask for a headless dictation worker rather than a normal launch.</summary>
    public static bool IsRequested(string[] args) => DictationWorkerProtocol.TryReadRequest(args, out _, out _, out _);

    /// <summary>Runs the dictation worker loop for the backend/model/language named in the arguments; returns the exit code.</summary>
    public static Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        DictationWorkerProtocol.TryReadRequest(args, out var backend, out var model, out var language);
        return DictationWorker.RunAsync(backend, model, language, cancellationToken);
    }
}

/// <summary>
/// The transcription worker, run headless in a child process (AC-174). It forces the chosen backend onto Whisper.net,
/// loads the model once, and then loops: read one clip's samples off stdin, transcribe it, write the text back. Because it
/// is its own process, a native <c>abort()</c> in the model load or in inference takes only this worker down — the desktop
/// respawns it and the app stays up. Mirrors <see cref="TranscriptionCalibrationProbe"/>'s native setup, but warm and
/// request-driven rather than one-shot.
/// </summary>
internal static class DictationWorker
{
    public static async Task<int> RunAsync(VoiceBackendPreference backend, string model, string language, CancellationToken cancellationToken)
    {
        WhisperProcessor? processor = null;
        WhisperFactory? factory = null;
        try
        {
            var host = WhisperRuntimeCache.CurrentPlatform
                       ?? throw new PlatformNotSupportedException("Whisper.net publishes no runtime for this OS.");

            var progress = new Progress<VoicePreparationProgress>(step => _Emit(new(DictationChildMessage.KindProgress, step.Description, step.Fraction)));

            // The native runtime loads once for this process — the crash-prone step is isolated here.
            var order = WhisperBackendPlanner.BuildOrder(backend, host);
            await WhisperRuntimeActivation.ApplyAsync(order, host, cancellationToken, logger: null, progress).ConfigureAwait(false);

            var modelType = WhisperModelCatalog.Resolve(model);
            var modelPath = await WhisperModelCache.EnsureDownloadedAsync(modelType, cancellationToken, logger: null, progress).ConfigureAwait(false);

            _Emit(new(DictationChildMessage.KindProgress, "Loading speech model…"));
            factory = WhisperFactory.FromPath(modelPath);
            processor = factory.CreateBuilder().WithLanguage(string.IsNullOrWhiteSpace(language) ? "auto" : language).Build();

            // Ready to take clips — the desktop unblocks its first dictation on this.
            _Emit(new(DictationChildMessage.KindReady));

            var input = Console.OpenStandardInput();
            while (!cancellationToken.IsCancellationRequested)
            {
                var samples = await _ReadClipAsync(input, cancellationToken).ConfigureAwait(false);
                if (samples is null || samples.Length == 0)
                {
                    // A zero-length clip or stdin EOF: the desktop is done with this worker.
                    return 0;
                }

                var text = new StringBuilder();
                await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
                {
                    text.Append(segment.Text);
                }

                _Emit(new(DictationChildMessage.KindResult, Text: DictationNoiseFilter.Strip(text.ToString())));
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 2;
        }
        catch (Exception exception)
        {
            _Emit(new(DictationChildMessage.KindError, exception.Message));
            return 1;
        }
        finally
        {
            if (processor is not null)
            {
                await processor.DisposeAsync().ConfigureAwait(false);
            }

            factory?.Dispose();
        }
    }

    // Reads one clip: an Int32 little-endian sample count, then that many little-endian float32 samples. Returns null on
    // stdin EOF (the pipe closed) so the caller exits cleanly.
    private static async Task<float[]?> _ReadClipAsync(Stream input, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        if (!await _ReadExactAsync(input, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (count <= 0)
        {
            return [];
        }

        var bytes = new byte[count * sizeof(float)];
        if (!await _ReadExactAsync(input, bytes, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * sizeof(float))));
        }

        return samples;
    }

    // Fills the whole buffer or returns false at EOF, so a clip is never read half-formed.
    private static async Task<bool> _ReadExactAsync(Stream input, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = await input.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (chunk == 0)
            {
                return false;
            }

            read += chunk;
        }

        return true;
    }

    private static void _Emit(DictationChildMessage message)
    {
        Console.Out.WriteLine(DictationWorkerProtocol.Encode(message));
        Console.Out.Flush();
    }
}
