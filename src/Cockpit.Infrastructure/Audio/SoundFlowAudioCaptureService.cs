using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Enums;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Audio;

namespace Cockpit.Infrastructure.Audio;

internal sealed class SoundFlowAudioCaptureService(AudioEngine engine, ILogger<SoundFlowAudioCaptureService> logger)
    : ISingletonService, IAudioCaptureService
{
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
        Core.Audio.AudioFormat format,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var nativeFormat = new SoundFlow.Structs.AudioFormat
        {
            SampleRate = format.SampleRate,
            Channels = format.Channels,
            Format = SampleFormat.S16,
        };

        var channel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        using var captureDevice = engine.InitializeCaptureDevice(deviceInfo: null, nativeFormat);
        logger.LogInformation("Capture device opened: {DeviceName}", captureDevice.Info?.Name ?? "(default)");

        captureDevice.OnAudioProcessed += OnAudioProcessed;
        captureDevice.Start();

        try
        {
            await foreach (var frame in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return frame;
            }
        }
        finally
        {
            captureDevice.OnAudioProcessed -= OnAudioProcessed;
            captureDevice.Stop();
        }

        yield break;

        void OnAudioProcessed(Span<float> samples, Capability capability)
        {
            if (capability != Capability.Record)
            {
                return;
            }

            var pcm = new byte[samples.Length * sizeof(short)];
            for (var i = 0; i < samples.Length; i++)
            {
                var clamped = Math.Clamp(samples[i], -1f, 1f);
                var sample = (short)(clamped * short.MaxValue);
                BitConverter.TryWriteBytes(pcm.AsSpan(i * sizeof(short), sizeof(short)), sample);
            }

            channel.Writer.TryWrite(pcm);
        }
    }
}
