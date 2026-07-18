using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<OpenMicCoordinator> _logger;

    private bool _wired;

    /// <summary>Whether read-aloud is playing right now — the only time a loud microphone means anything to this coordinator.</summary>
    private bool _isPlaying;

    // Read when listening starts rather than per frame: a level fires many times a second, and the silence
    // timeout next to these is read once at the same point for the same reason.
    private bool _stopReadAloudWhenSpeaking;
    private double _stopReadAloudThreshold;

    public OpenMicCoordinator(
        IOpenMicListener listener,
        CockpitViewModel cockpit,
        IVoiceSettingsStore voiceSettingsStore,
        ITranscriptCleanupService cleanup,
        IVoicePlaybackQueue playbackQueue,
        VoiceOverlayCoordinator overlay,
        ILogger<OpenMicCoordinator> logger)
    {
        _listener = listener;
        _cockpit = cockpit;
        _voiceSettingsStore = voiceSettingsStore;
        _cleanup = cleanup;
        _playbackQueue = playbackQueue;
        _overlay = overlay;
        _logger = logger;

        // Subscribed for the singleton's whole life, not only while open-mic is on: read-aloud can play without
        // dictation ever being enabled (the per-session toggle, the Options "Test" button), and its "speaking" pill
        // must still show. Barge-in (pausing the mic) is gated on actually listening inside the handler.
        _playbackQueue.PlaybackActiveChanged += _OnPlaybackActiveChanged;
    }

    /// <summary>True once voice is enabled — open-mic needs the mic pipeline, so the toggle is disabled until then.</summary>
    [ObservableProperty]
    private bool _isAvailable;

    /// <summary>True while the mic is actively listening; drives the toggle button's on/off state.</summary>
    [ObservableProperty]
    private bool _isListening;

    /// <summary>Reads settings at startup and resumes listening if open-mic was left on; runtime toggling is via <see cref="ToggleOpenMicCommand"/>. No-op when voice is off.</summary>
    /// <remarks>
    /// Never throws, for the reason <see cref="VoicePushToTalkCoordinator.StartAsync"/> does not: its one caller
    /// discards the task, so anything thrown here lands on a task nobody observes. It still cannot start when the
    /// settings will not read or the microphone will not open — it says which now.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await _voiceSettingsStore.LoadAsync(cancellationToken);
            IsAvailable = settings.IsEnabled;
            if (settings.IsEnabled && settings.OpenMicEnabled)
            {
                await _EnableAsync(cancellationToken);
            }
        }
        catch (Exception exception)
        {
            // Leave nothing subscribed to a listener that never started. IsAvailable is deliberately left as the
            // settings read found it: voice being on is what the toggle is gated on, and a microphone that failed
            // to open is the operator's to retry — greying the toggle out would take that away over one bad start.
            _Unwire();

            _logger.LogError(exception, "Open-mic dictation could not start; the microphone is not listening.");
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

        var settings = await _voiceSettingsStore.LoadAsync(cancellationToken);
        _stopReadAloudWhenSpeaking = settings.StopReadAloudWhenSpeaking;
        _stopReadAloudThreshold = settings.StopReadAloudLevelThreshold;

        if (!_wired)
        {
            _listener.UtteranceTranscribed += _OnUtteranceTranscribed;
            _listener.SpeechStarted += _OnSpeechStarted;
            _listener.SpeechEnded += _OnSpeechEnded;
            _listener.AudioLevelSampled += _OnAudioLevelSampled;
            _wired = true;
        }

        await _listener.StartAsync(cancellationToken);
        IsListening = true;
    }

    private void _Unwire()
    {
        if (!_wired)
        {
            return;
        }

        _listener.UtteranceTranscribed -= _OnUtteranceTranscribed;
        _listener.SpeechStarted -= _OnSpeechStarted;
        _listener.SpeechEnded -= _OnSpeechEnded;
        _listener.AudioLevelSampled -= _OnAudioLevelSampled;
        _wired = false;
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

    // Barge-in: pause the mic while read-aloud plays, resume once the queue goes idle — but only while actually
    // listening. The overlay's "speaking" pill is surfaced unconditionally (HandlePlaybackActiveChanged), so it
    // shows for read-aloud even when open-mic is off.
    private void _OnPlaybackActiveChanged(object? sender, bool active)
    {
        if (IsListening)
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

        Dispatcher.UIThread.Post(() => HandlePlaybackActiveChanged(active));
    }

    private void _OnSpeechStarted(object? sender, EventArgs e) => Dispatcher.UIThread.Post(HandleSpeechStarted);

    private void _OnSpeechEnded(object? sender, EventArgs e) => Dispatcher.UIThread.Post(HandleSpeechEnded);

    private void _OnAudioLevelSampled(object? sender, double level) => Dispatcher.UIThread.Post(() => HandleAudioLevel(level));

    /// <summary>
    /// Test seam: the VAD heard speech start. Open-mic listens the whole time it is on, so this — not
    /// <see cref="StartAsync"/> — is the moment there is something to show: a pill that appeared when you
    /// switched open-mic on and sat there would only be saying the feature is on.
    /// </summary>
    internal void HandleSpeechStarted() => _overlay.SetOpenMic(VoiceOverlayState.Listening);

    /// <summary>Test seam: the utterance is over and about to be transcribed — the part worth a spinner.</summary>
    internal void HandleSpeechEnded() => _overlay.SetOpenMic(VoiceOverlayState.Transcribing);

    /// <summary>Test seam: read-aloud started or stopped. The pill is how the operator sees why their microphone just went quiet.</summary>
    internal void HandlePlaybackActiveChanged(bool active)
    {
        _isPlaying = active;
        _overlay.SetSpeaking(active);
    }

    /// <summary>
    /// Test seam: one microphone level. Feeds the pill's waveform, and — when the operator asked for it — stops
    /// read-aloud the moment they start talking over it (AC-9).
    /// </summary>
    /// <remarks>
    /// The level arrives even while the listener is paused for playback: the capture loop keeps reading and only
    /// the VAD is skipped, which is what makes hearing the operator during read-aloud possible at all without a
    /// second microphone path.
    /// <para>
    /// A hold already stops playback the same way (<c>SessionPanelViewModel.BeginVoiceHold</c>) and needs no
    /// threshold, because a key being held is not something a room does by accident. This is the half that has to
    /// guess, which is why it is off unless asked for — see <see cref="Cockpit.Core.Voice.VoiceSettings.StopReadAloudWhenSpeaking"/>.
    /// </para>
    /// </remarks>
    internal void HandleAudioLevel(double level)
    {
        _overlay.PushLevel(level);

        if (_isPlaying && _stopReadAloudWhenSpeaking && level >= _stopReadAloudThreshold)
        {
            // Stopping flips playback to idle, which resumes the listener — so what you said next is dictated
            // rather than talked over. The frames until that lands are a repeat of this call and StopAll's own
            // no-op, not a second interruption.
            _playbackQueue.StopAll();
        }
    }

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

            var text = session is TtyViewModel ? rawText : await _cleanup.CleanupAsync(rawText);
            session.InjectVoiceTranscript(text);
        }
        finally
        {
            _overlay.SetOpenMic(null);
        }
    }

    partial void OnIsAvailableChanged(bool value) => ToggleOpenMicCommand.NotifyCanExecuteChanged();
}
