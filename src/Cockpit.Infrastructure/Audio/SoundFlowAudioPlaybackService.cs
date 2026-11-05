using Microsoft.Extensions.Logging;
using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Providers;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

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
    private string? _resolvedDeviceName;
    private VoiceSettings? _settings;
    private int _sampleRate;
    private int _channels;
    private CancellationTokenSource? _idleCloseCancellation;
    private static readonly TimeSpan IdleCloseDelay = TimeSpan.FromSeconds(5);

    public async Task PlayAsync(
        ReadOnlyMemory<byte> pcm,
        Core.Audio.AudioFormat format,
        CancellationToken cancellationToken = default)
    {
        await _playbackGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _CancelIdleClose();
            var nativeFormat = new SoundFlow.Structs.AudioFormat
            {
                SampleRate = format.SampleRate,
                Channels = format.Channels,
                Format = SampleFormat.S16,
            };

            var settings = _settings ??= await voiceSettingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var selectedDevice = deviceEnumerator.ResolveOutputDevice(settings.OutputDeviceName);
            var resolvedDeviceName = selectedDevice?.Name;
            if (_playbackDevice is null
                || !string.Equals(_outputDeviceName, settings.OutputDeviceName, StringComparison.Ordinal)
                || !string.Equals(_resolvedDeviceName, resolvedDeviceName, StringComparison.Ordinal)
                || _sampleRate != format.SampleRate
                || _channels != format.Channels)
            {
                _playbackDevice?.Dispose();
                _playbackDevice = engine.InitializePlaybackDevice(selectedDevice, nativeFormat);
                _outputDeviceName = settings.OutputDeviceName;
                _resolvedDeviceName = resolvedDeviceName;
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
        catch
        {
            // A removed device can dispose itself under SoundFlow, so the next clip must reopen output.
            _CloseDevice();
            throw;
        }
        finally
        {
            _playbackGate.Release();
        }
        _ScheduleIdleClose();
    }

    private void _ScheduleIdleClose()
    {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _idleCloseCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        _ = _CloseAfterIdleAsync(cancellation);
    }

    private async Task _CloseAfterIdleAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(IdleCloseDelay, cancellation.Token).ConfigureAwait(false);
            await _playbackGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(cancellation, _idleCloseCancellation))
                {
                    _CloseDevice();
                }
            }
            finally
            {
                _playbackGate.Release();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void _CancelIdleClose()
    {
        var previous = Interlocked.Exchange(ref _idleCloseCancellation, null);
        previous?.Cancel();
        previous?.Dispose();
    }

    private void _CloseDevice()
    {
        _playbackDevice?.Dispose();
        _playbackDevice = null;
        _outputDeviceName = null;
        _resolvedDeviceName = null;
        _sampleRate = 0;
        _channels = 0;
    }

    public void Dispose()
    {
        _CancelIdleClose();
        _CloseDevice();
        _playbackGate.Dispose();
    }
}
