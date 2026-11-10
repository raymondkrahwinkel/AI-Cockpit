using System.Threading.Channels;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Audio;
using Cockpit.Core.Voice;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Services;

public sealed class AudioDuckingCoordinator(
    IVoicePlaybackQueue playback,
    IVoiceSettingsStore settingsStore,
    ISystemAudioDucker ducker,
    IAudioAvailability availability,
    CockpitViewModel cockpit,
    ILogger<AudioDuckingCoordinator> logger) : ISingletonService, IAsyncDisposable
{
    private sealed record Signal(bool Speaking = false, bool SettingsChanged = false, int Generation = 0, DateTimeOffset? StoppedAt = null);

    private readonly Channel<Signal> _signals = Channel.CreateUnbounded<Signal>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stopping = new();
    private Task? _worker;
    private int _generation;
    private bool _active;
    private bool _disposed;

    public Task StartAsync()
    {
        playback.SpeakingStarted += _OnSpeakingStarted;
        playback.PlaybackActiveChanged += _OnPlaybackActiveChanged;
        cockpit.VoiceSettingsSaved += _OnSettingsSaved;
        _signals.Writer.TryWrite(new Signal(SettingsChanged: true));
        _worker = Task.Run(_ConsumeAsync);
        return Task.CompletedTask;
    }

    private void _OnSpeakingStarted(object? sender, EventArgs args)
    {
        Interlocked.Increment(ref _generation);
        _signals.Writer.TryWrite(new Signal(Speaking: true));
    }

    private async void _OnPlaybackActiveChanged(object? sender, bool active)
    {
        if (active)
        {
            return;
        }
        int generation = Volatile.Read(ref _generation);
        DateTimeOffset stoppedAt = DateTimeOffset.UtcNow;
        try
        {
            await Task.Delay(AudioDuckingPolicy.ReleaseDelay, _stopping.Token).ConfigureAwait(false);
            _signals.Writer.TryWrite(new Signal(Generation: generation, StoppedAt: stoppedAt));
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    }

    private void _OnSettingsSaved(object? sender, EventArgs args) =>
        _signals.Writer.TryWrite(new Signal(SettingsChanged: true));

    private async Task _ConsumeAsync()
    {
        try
        {
            await foreach (Signal signal in _signals.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
            {
                if (signal.Speaking || signal.SettingsChanged)
                {
                    VoiceSettings settings = await settingsStore.LoadAsync(_stopping.Token).ConfigureAwait(false);
                    bool enabled = availability.IsAvailable && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                        && settings.DuckOtherAudioWhileSpeaking;
                    if (signal.SettingsChanged && (enabled && !_active || !enabled && _active))
                    {
                        await ducker.RestoreAsync(_stopping.Token).ConfigureAwait(false);
                        _active = false;
                    }
                    if (signal.Speaking)
                    {
                        if (AudioDuckingPolicy.ShouldStart(enabled, _active))
                        {
                            _active = true;
                            await ducker.RestoreAsync(_stopping.Token).ConfigureAwait(false);
                            await ducker.DuckAsync(settings.DuckingLevel, _stopping.Token).ConfigureAwait(false);
                        }
                    }
                }
                else if (signal.Generation == Volatile.Read(ref _generation) && _active)
                {
                    if (AudioDuckingPolicy.ReleaseDue(DateTimeOffset.UtcNow, signal.StoppedAt))
                    {
                        await ducker.RestoreAsync(_stopping.Token).ConfigureAwait(false);
                        _active = false;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Audio ducking coordination stopped; read-aloud remains available.");
        }
        finally
        {
            if (_active)
            {
                await ducker.RestoreAsync().ConfigureAwait(false);
                _active = false;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        playback.SpeakingStarted -= _OnSpeakingStarted;
        playback.PlaybackActiveChanged -= _OnPlaybackActiveChanged;
        cockpit.VoiceSettingsSaved -= _OnSettingsSaved;
        _stopping.Cancel();
        _signals.Writer.TryComplete();
        if (_worker is not null)
        {
            await _worker.ConfigureAwait(false);
        }
    }
}
