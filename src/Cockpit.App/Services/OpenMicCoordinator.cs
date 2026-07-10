using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.App.Services;

/// <summary>
/// Drives open-mic dictation: when voice and open-mic are enabled, starts the continuous
/// <see cref="IOpenMicListener"/>, injects each finished utterance into the currently selected session
/// (SDK sessions get the Ollama cleanup pass, TTY gets the raw text — the same split push-to-talk makes),
/// and pauses the mic while read-aloud is playing so it never transcribes the cockpit's own speech.
/// </summary>
/// <remarks>
/// Threading mirrors <see cref="VoicePushToTalkCoordinator"/>: <see cref="IOpenMicListener.UtteranceTranscribed"/>
/// fires on the capture thread, so injection is marshaled onto the UI thread via
/// <see cref="Dispatcher.UIThread"/>. <see cref="InjectUtteranceAsync"/> is the (UI-thread) logic the tests
/// drive directly, since pumping a real Avalonia dispatcher loop from a unit test is not practical.
/// </remarks>
public sealed class OpenMicCoordinator : ISingletonService
{
    private readonly IOpenMicListener _listener;
    private readonly CockpitViewModel _cockpit;
    private readonly IVoiceSettingsStore _voiceSettingsStore;
    private readonly ITranscriptCleanupService _cleanup;
    private readonly IVoicePlaybackQueue _playbackQueue;

    public OpenMicCoordinator(
        IOpenMicListener listener,
        CockpitViewModel cockpit,
        IVoiceSettingsStore voiceSettingsStore,
        ITranscriptCleanupService cleanup,
        IVoicePlaybackQueue playbackQueue)
    {
        _listener = listener;
        _cockpit = cockpit;
        _voiceSettingsStore = voiceSettingsStore;
        _cleanup = cleanup;
        _playbackQueue = playbackQueue;
    }

    /// <summary>Starts open-mic listening. No-op when voice or open-mic is off, so the mic is never opened for an operator who never opted in.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _voiceSettingsStore.LoadAsync(cancellationToken);
        if (!settings.IsEnabled || !settings.OpenMicEnabled)
        {
            return;
        }

        _listener.UtteranceTranscribed += _OnUtteranceTranscribed;
        _playbackQueue.PlaybackActiveChanged += _OnPlaybackActiveChanged;
        await _listener.StartAsync(cancellationToken);
    }

    // Barge-in: pause the mic while read-aloud plays, resume once the queue goes idle.
    private void _OnPlaybackActiveChanged(object? sender, bool active)
    {
        if (active)
        {
            _listener.Pause();
        }
        else
        {
            _listener.Resume();
        }
    }

    private void _OnUtteranceTranscribed(object? sender, string rawText) =>
        Dispatcher.UIThread.Post(() => _ = InjectUtteranceAsync(rawText));

    /// <summary>Test seam: the UI-thread logic that cleans (for SDK sessions) and injects an utterance into the selected session.</summary>
    internal async Task InjectUtteranceAsync(string rawText)
    {
        var session = _cockpit.SelectedSession;
        if (session is null)
        {
            return;
        }

        var text = session is ClaudeTtyViewModel ? rawText : await _cleanup.CleanupAsync(rawText);
        session.InjectVoiceTranscript(text);
    }
}
