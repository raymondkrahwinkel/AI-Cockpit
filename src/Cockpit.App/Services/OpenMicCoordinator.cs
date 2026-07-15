using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.App.Services;

/// <summary>
/// Drives open-mic dictation and exposes a runtime on/off toggle (bound to a sidebar button): when
/// listening, the continuous <see cref="IOpenMicListener"/> injects each finished utterance into the
/// currently selected session (SDK sessions get the Ollama cleanup pass, TTY gets the raw text — the same
/// split push-to-talk makes), and the mic pauses while read-aloud is playing so it never transcribes the
/// cockpit's own speech. The on/off state is persisted, so it resumes next launch.
/// </summary>
/// <remarks>
/// Threading mirrors <see cref="VoicePushToTalkCoordinator"/>: <see cref="IOpenMicListener.UtteranceTranscribed"/>
/// fires on the capture thread, so injection is marshaled onto the UI thread via
/// <see cref="Dispatcher.UIThread"/>. <see cref="InjectUtteranceAsync"/> is the (UI-thread) logic the tests
/// drive directly, since pumping a real Avalonia dispatcher loop from a unit test is not practical.
/// </remarks>
public sealed partial class OpenMicCoordinator : ObservableObject, ISingletonService
{
    private readonly IOpenMicListener _listener;
    private readonly CockpitViewModel _cockpit;
    private readonly IVoiceSettingsStore _voiceSettingsStore;
    private readonly ITranscriptCleanupService _cleanup;
    private readonly IVoicePlaybackQueue _playbackQueue;
    private readonly VoiceOverlayCoordinator _overlay;

    private bool _wired;

    public OpenMicCoordinator(
        IOpenMicListener listener,
        CockpitViewModel cockpit,
        IVoiceSettingsStore voiceSettingsStore,
        ITranscriptCleanupService cleanup,
        IVoicePlaybackQueue playbackQueue,
        VoiceOverlayCoordinator overlay)
    {
        _listener = listener;
        _cockpit = cockpit;
        _voiceSettingsStore = voiceSettingsStore;
        _cleanup = cleanup;
        _playbackQueue = playbackQueue;
        _overlay = overlay;
    }

    /// <summary>True once voice is enabled — open-mic needs the mic pipeline, so the toggle is disabled until then.</summary>
    [ObservableProperty]
    private bool _isAvailable;

    /// <summary>True while the mic is actively listening; drives the toggle button's on/off state.</summary>
    [ObservableProperty]
    private bool _isListening;

    /// <summary>Reads settings at startup and resumes listening if open-mic was left on; runtime toggling is via <see cref="ToggleOpenMicCommand"/>. No-op when voice is off.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _voiceSettingsStore.LoadAsync(cancellationToken);
        IsAvailable = settings.IsEnabled;
        if (settings.IsEnabled && settings.OpenMicEnabled)
        {
            await _EnableAsync(cancellationToken);
        }
    }

    /// <summary>Runtime on/off, gated on voice being enabled; persists the state so it is remembered next launch.</summary>
    [RelayCommand(CanExecute = nameof(IsAvailable))]
    private async Task ToggleOpenMic()
    {
        if (IsListening)
        {
            await _DisableAsync();
        }
        else
        {
            await _EnableAsync();
        }

        var settings = await _voiceSettingsStore.LoadAsync();
        await _voiceSettingsStore.SaveAsync(settings with { OpenMicEnabled = IsListening });
    }

    private async Task _EnableAsync(CancellationToken cancellationToken = default)
    {
        if (IsListening)
        {
            return;
        }

        if (!_wired)
        {
            _listener.UtteranceTranscribed += _OnUtteranceTranscribed;
            _listener.SpeechStarted += _OnSpeechStarted;
            _listener.SpeechEnded += _OnSpeechEnded;
            _listener.AudioLevelSampled += _OnAudioLevelSampled;
            _playbackQueue.PlaybackActiveChanged += _OnPlaybackActiveChanged;
            _wired = true;
        }

        await _listener.StartAsync(cancellationToken);
        IsListening = true;
    }

    private async Task _DisableAsync()
    {
        if (!IsListening)
        {
            return;
        }

        await _listener.StopAsync();
        IsListening = false;

        // Turned off mid-sentence, the pill would otherwise sit on whatever the last utterance left it.
        _overlay.SetOpenMic(null);
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

        Dispatcher.UIThread.Post(() => HandlePlaybackActiveChanged(active));
    }

    private void _OnSpeechStarted(object? sender, EventArgs e) => Dispatcher.UIThread.Post(HandleSpeechStarted);

    private void _OnSpeechEnded(object? sender, EventArgs e) => Dispatcher.UIThread.Post(HandleSpeechEnded);

    private void _OnAudioLevelSampled(object? sender, double level) => Dispatcher.UIThread.Post(() => _overlay.PushLevel(level));

    /// <summary>
    /// Test seam: the VAD heard speech start. Open-mic listens the whole time it is on, so this — not
    /// <see cref="StartAsync"/> — is the moment there is something to show: a pill that appeared when you
    /// switched open-mic on and sat there would only be saying the feature is on.
    /// </summary>
    internal void HandleSpeechStarted() => _overlay.SetOpenMic(VoiceOverlayState.Listening);

    /// <summary>Test seam: the utterance is over and about to be transcribed — the part worth a spinner.</summary>
    internal void HandleSpeechEnded() => _overlay.SetOpenMic(VoiceOverlayState.Transcribing);

    /// <summary>Test seam: read-aloud started or stopped. The pill is how the operator sees why their microphone just went quiet.</summary>
    internal void HandlePlaybackActiveChanged(bool active) => _overlay.SetSpeaking(active);

    private void _OnUtteranceTranscribed(object? sender, string rawText) =>
        Dispatcher.UIThread.Post(() => _ = InjectUtteranceAsync(rawText));

    /// <summary>Test seam: the UI-thread logic that cleans (for SDK sessions) and injects an utterance into the selected session.</summary>
    /// <remarks>
    /// The pill is released here rather than on <c>SpeechEnded</c>: the cleanup pass runs between the two, and a
    /// spinner that stops before the text lands would be a spinner that lied about the last part of the wait.
    /// Released in a finally — an utterance that fails to clean up or inject still ends, and the alternative is a
    /// pill spinning over a sentence that is never coming.
    /// </remarks>
    internal async Task InjectUtteranceAsync(string rawText)
    {
        try
        {
            var session = _cockpit.SelectedSession;
            if (session is null)
            {
                return;
            }

            var text = session is ClaudeTtyViewModel ? rawText : await _cleanup.CleanupAsync(rawText);
            session.InjectVoiceTranscript(text);
        }
        finally
        {
            _overlay.SetOpenMic(null);
        }
    }

    partial void OnIsAvailableChanged(bool value) => ToggleOpenMicCommand.NotifyCanExecuteChanged();
}
