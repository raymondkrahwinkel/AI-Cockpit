using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SoundFlow.Abstracts;
using Cockpit.Core.Abstractions.Audio;

namespace Cockpit.App;

/// <summary>
/// Headless harness proving the F0 audio pipeline: enumerate devices, capture ~2s of 16 kHz mono
/// PCM, print an RMS level, then play back a test tone followed by the recorded buffer.
/// </summary>
internal static class AudioSpike
{
    private static readonly Core.Audio.AudioFormat Format = new();

    public static async Task RunAsync(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        var engine = services.GetRequiredService<AudioEngine>();
        var captureService = services.GetRequiredService<IAudioCaptureService>();
        var playbackService = services.GetRequiredService<IAudioPlaybackService>();

        engine.UpdateAudioDevicesInfo();
        logger.LogInformation("Playback devices: {Devices}", string.Join(", ", engine.PlaybackDevices.Select(d => d.Name)));
        logger.LogInformation("Capture devices: {Devices}", string.Join(", ", engine.CaptureDevices.Select(d => d.Name)));

        logger.LogInformation("Recording ~2s of {SampleRate} Hz mono s16 PCM...", Format.SampleRate);
        using var recordingCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var recordedPcm = new List<byte>();

        try
        {
            await foreach (var frame in captureService.CaptureAsync(Format, recordingCancellation.Token))
            {
                recordedPcm.AddRange(frame.ToArray());
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the 2s timeout stops the capture stream.
        }

        var pcm = recordedPcm.ToArray();
        var durationSeconds = pcm.Length / (double)(Format.SampleRate * Format.Channels * (Format.BitsPerSample / 8));
        logger.LogInformation(
            "Captured {Bytes} bytes ({Duration:F2}s), RMS level: {Rms:F4}",
            pcm.Length,
            durationSeconds,
            CalculateRms(pcm));

        logger.LogInformation("Playing a 440 Hz test tone (~1s)...");
        await playbackService.PlayAsync(GenerateSineWave(440, TimeSpan.FromSeconds(1)), Format);

        logger.LogInformation("Playing back the recorded buffer...");
        await playbackService.PlayAsync(pcm, Format);

        logger.LogInformation("Playback done.");
    }

    private static double CalculateRms(byte[] pcm)
    {
        if (pcm.Length < sizeof(short))
        {
            return 0;
        }

        var sampleCount = pcm.Length / sizeof(short);
        double sumOfSquares = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = BitConverter.ToInt16(pcm, i * sizeof(short));
            var normalized = sample / (double)short.MaxValue;
            sumOfSquares += normalized * normalized;
        }

        return Math.Sqrt(sumOfSquares / sampleCount);
    }

    private static byte[] GenerateSineWave(double frequencyHz, TimeSpan duration)
    {
        var sampleCount = (int)(Format.SampleRate * duration.TotalSeconds);
        var pcm = new byte[sampleCount * sizeof(short)];

        for (var i = 0; i < sampleCount; i++)
        {
            var t = i / (double)Format.SampleRate;
            var sample = (short)(Math.Sin(2 * Math.PI * frequencyHz * t) * short.MaxValue * 0.5);
            BitConverter.TryWriteBytes(pcm.AsSpan(i * sizeof(short), sizeof(short)), sample);
        }

        return pcm;
    }
}
