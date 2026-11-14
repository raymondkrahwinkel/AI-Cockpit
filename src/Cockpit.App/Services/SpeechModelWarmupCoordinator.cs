using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Voice;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Services;

// AC-2350: with "Keep speech models loaded" on, loads the Whisper worker and the TTS voice in the background shortly
// after startup, and again when the option is switched on. Keeping them loaded is the Whisper service's idle check.
public sealed class SpeechModelWarmupCoordinator(
    IVoicePushToTalkService pushToTalk,
    ISpeechToTextService speechToText,
    IVoicePlaybackQueue playback,
    IVoiceSettingsStore settingsStore,
    IAudioAvailability availability,
    CockpitViewModel cockpit,
    ILogger<SpeechModelWarmupCoordinator> logger) : ISingletonService
{
    private const int WarmUpClipSamples = 8_000; // 0.5 s at 16 kHz

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    public void Start()
    {
        cockpit.VoiceSettingsSaved += (_, _) => _ = Task.Run(_WarmUpAsync);
        _ = Task.Run(async () =>
        {
            await Task.Delay(StartupDelay).ConfigureAwait(false);
            await _WarmUpAsync().ConfigureAwait(false);
        });
    }

    // The first inference after the model loads is far slower than the next ones, so one throwaway clip pays it now.
    // Silence does not: measured, Whisper skips it and the first real clip stays slow. Only here, not in the hotkey
    // warm-up. Result discarded.
    private async Task _WarmUpDictationAsync()
    {
        await pushToTalk.WarmUpAsync().ConfigureAwait(false);
        await speechToText.TranscribeAsync(_WarmUpClip()).ConfigureAwait(false);
    }

    private static float[] _WarmUpClip()
    {
        var random = new Random(1);
        var samples = new float[WarmUpClipSamples];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)((0.2 * Math.Sin(2 * Math.PI * 220 * i / 16_000)) + (0.05 * (random.NextDouble() - 0.5)));
        }

        return samples;
    }

    private async Task _WarmUpAsync()
    {
        try
        {
            var settings = await settingsStore.LoadAsync().ConfigureAwait(false);
            if (!settings.KeepModelsLoaded || !availability.IsAvailable)
            {
                return;
            }

            // Each WarmUpAsync swallows its own failure; the first real use reports it.
            await Task.WhenAll(
                settings.IsEnabled ? _WarmUpDictationAsync() : Task.CompletedTask,
                playback.WarmUpAsync()).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Warming the speech models failed; they load on first use instead.");
        }
    }
}
