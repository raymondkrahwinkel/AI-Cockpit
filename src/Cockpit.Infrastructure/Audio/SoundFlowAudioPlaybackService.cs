using Microsoft.Extensions.Logging;
using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Providers;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.Infrastructure.Audio;

internal sealed class SoundFlowAudioPlaybackService(
    AudioEngine engine,
    AudioDeviceEnumerator deviceEnumerator,
    IVoiceSettingsStore voiceSettingsStore,
    ILogger<SoundFlowAudioPlaybackService> logger)
    : IAudioPlaybackService, IDisposable
{
    private readonly SemaphoreSlim _playbackGate = new(1, 1);
    private AudioPlaybackDevice? _playbackDevice;
    private string? _outputDeviceName;
    private int _sampleRate;
    private int _channels;

    public async Task PlayAsync(
        ReadOnlyMemory<byte> pcm,
        Core.Audio.AudioFormat format,
        CancellationToken cancellationToken = default)
    {
        await _playbackGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var nativeFormat = new SoundFlow.Structs.AudioFormat
            {
                SampleRate = format.SampleRate,
                Channels = format.Channels,
                Format = SampleFormat.S16,
            };

            var settings = await voiceSettingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var selectedDevice = deviceEnumerator.ResolveOutputDevice(settings.OutputDeviceName);
            if (_playbackDevice is null
                || !string.Equals(_outputDeviceName, settings.OutputDeviceName, StringComparison.Ordinal)
                || _sampleRate != format.SampleRate
                || _channels != format.Channels)
            {
                _playbackDevice?.Dispose();
                _playbackDevice = engine.InitializePlaybackDevice(selectedDevice, nativeFormat);
                _outputDeviceName = settings.OutputDeviceName;
                _sampleRate = format.SampleRate;
                _channels = format.Channels;
                _playbackDevice.Start();
                logger.LogInformation("Playback device opened: {DeviceName}", _playbackDevice.Info?.Name ?? "(default)");
            }

            using var dataProvider = new RawDataProvider(pcm.ToArray(), SampleFormat.S16, format.SampleRate);
            using var player = new SoundPlayer(engine, nativeFormat, dataProvider);
            _playbackDevice.MasterMixer.AddComponent(player);

            var playbackCompleted = new TaskCompletionSource();
            player.PlaybackEnded += (_, _) => playbackCompleted.TrySetResult();
            try
            {
                player.Play();
                await using (cancellationToken.Register(() => playbackCompleted.TrySetCanceled(cancellationToken)))
                {
                    await playbackCompleted.Task;
                }
            }
            finally
            {
                _playbackDevice.MasterMixer.RemoveComponent(player);
            }
        }
        finally
        {
            _playbackGate.Release();
        }
    }

    public void Dispose()
    {
        _playbackDevice?.Dispose();
        _playbackGate.Dispose();
    }
}
