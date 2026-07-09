using Microsoft.Extensions.Logging;
using SoundFlow.Abstracts;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Providers;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.Infrastructure.Audio;

internal sealed class SoundFlowAudioPlaybackService(
    AudioEngine engine,
    AudioDeviceEnumerator deviceEnumerator,
    IVoiceSettingsStore voiceSettingsStore,
    ILogger<SoundFlowAudioPlaybackService> logger)
    : ISingletonService, IAudioPlaybackService
{
    // miniaudio races the last audio callback against Stop()/Dispose() right after natural
    // end-of-stream: stopping the device immediately hangs indefinitely. A short grace period
    // lets the backend's final callback settle before we tear the device down.
    private static readonly TimeSpan PostPlaybackStopDelay = TimeSpan.FromMilliseconds(150);

    public async Task PlayAsync(
        ReadOnlyMemory<byte> pcm,
        Core.Audio.AudioFormat format,
        CancellationToken cancellationToken = default)
    {
        var nativeFormat = new SoundFlow.Structs.AudioFormat
        {
            SampleRate = format.SampleRate,
            Channels = format.Channels,
            Format = SampleFormat.S16,
        };

        var settings = await voiceSettingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var selectedDevice = deviceEnumerator.ResolveOutputDevice(settings.OutputDeviceName);

        using var playbackDevice = engine.InitializePlaybackDevice(selectedDevice, nativeFormat);
        logger.LogInformation("Playback device opened: {DeviceName}", playbackDevice.Info?.Name ?? "(default)");

        using var dataProvider = new RawDataProvider(pcm.ToArray(), SampleFormat.S16, format.SampleRate);
        using var player = new SoundPlayer(engine, nativeFormat, dataProvider);

        playbackDevice.Start();
        playbackDevice.MasterMixer.AddComponent(player);

        var playbackCompleted = new TaskCompletionSource();
        player.PlaybackEnded += (_, _) => playbackCompleted.TrySetResult();

        player.Play();

        await using (cancellationToken.Register(() => playbackCompleted.TrySetCanceled(cancellationToken)))
        {
            await playbackCompleted.Task;
        }

        await Task.Delay(PostPlaybackStopDelay, CancellationToken.None);

        playbackDevice.MasterMixer.RemoveComponent(player);
        playbackDevice.Stop();
    }
}
