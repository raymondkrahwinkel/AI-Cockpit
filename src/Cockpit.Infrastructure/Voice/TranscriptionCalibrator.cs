using System.Diagnostics;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// Runs the first-use calibration (AC-68 slice 3): transcribes a fixed synthetic clip on the configured backend a
/// few times, timing it, while a UI-thread hitch probe samples how much the desktop stutters. The clip's content
/// is irrelevant — only the time it takes and the hitch it causes matter — so nothing has to be bundled. The
/// result is stored for this machine and returned so the Options page can show it and, if the GPU hitched, steer
/// to the CPU.
/// </summary>
internal sealed class TranscriptionCalibrator(
    ISpeechToTextService speechToText,
    IUiHitchProbe hitchProbe,
    ITranscriptionCalibrationStore store,
    IVoiceSettingsStore settingsStore) : ITranscriptionCalibrator, ISingletonService
{
    private const int SampleRate = 16000;
    private const int ClipSeconds = 4;
    private const int MeasuredRuns = 3;

    public async Task<TranscriptionCalibration> MeasureAsync(IProgress<string>? status = null, CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var clip = _CalibrationClip();

        // Warm up first: the first transcription loads the runtime and model (and may download it) — timing that
        // would measure the download, not the machine.
        status?.Report("Preparing model…");
        await speechToText.TranscribeAsync(clip, cancellationToken).ConfigureAwait(false);

        status?.Report("Measuring…");
        var latencies = new List<double>(MeasuredRuns);
        double hitchMs;
        using (var session = hitchProbe.Start())
        {
            for (var run = 0; run < MeasuredRuns; run++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stopwatch = Stopwatch.StartNew();
                await speechToText.TranscribeAsync(clip, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
            }

            hitchMs = session.MaxHitchMs;
        }

        latencies.Sort();
        var medianLatency = latencies[latencies.Count / 2];
        var backend = _ResolveBackend(speechToText.ActiveBackend, settings.BackendPreference);

        var calibration = new TranscriptionCalibration(medianLatency, hitchMs, backend, settings.ModelName);
        await store.SaveAsync(calibration, cancellationToken).ConfigureAwait(false);
        return calibration;
    }

    // The native runtime that actually loaded, mapped to the user-facing three-way. Falls back to the preference
    // (Auto → CPU) when the loader has not recorded one yet.
    private static VoiceBackendPreference _ResolveBackend(WhisperRuntimeBackend? active, VoiceBackendPreference preference) => active switch
    {
        WhisperRuntimeBackend.Cuda or WhisperRuntimeBackend.Cuda12 => VoiceBackendPreference.Cuda,
        WhisperRuntimeBackend.Vulkan => VoiceBackendPreference.Vulkan,
        WhisperRuntimeBackend.Cpu or WhisperRuntimeBackend.CpuNoAvx => VoiceBackendPreference.Cpu,
        _ => preference is VoiceBackendPreference.Auto ? VoiceBackendPreference.Cpu : preference,
    };

    // A few gliding tones under a syllable-rate amplitude envelope: enough to make the encoder and decoder do real
    // work (so the timing is representative), without bundling an audio asset. 16 kHz mono float32, the STT input.
    private static float[] _CalibrationClip()
    {
        var samples = new float[SampleRate * ClipSeconds];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / SampleRate;
            var envelope = 0.5 * (1 - Math.Cos(2 * Math.PI * (t * 4 % 1)));
            var tone = Math.Sin(2 * Math.PI * 140 * t)
                       + 0.5 * Math.Sin(2 * Math.PI * 400 * t)
                       + 0.3 * Math.Sin(2 * Math.PI * 900 * t);
            samples[i] = (float)(0.2 * envelope * tone);
        }

        return samples;
    }
}
