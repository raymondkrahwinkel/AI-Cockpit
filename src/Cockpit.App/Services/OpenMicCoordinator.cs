using System.Threading.Channels;
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
public sealed partial class OpenMicCoordinator : ObservableObject, ISingletonService, IOpenMicState
{
    private readonly IOpenMicListener _listener;
    private readonly CockpitViewModel _cockpit;
    private readonly IVoiceSettingsStore _voiceSettingsStore;
    private readonly ITranscriptCleanupService _cleanup;
    private readonly IVoicePlaybackQueue _playbackQueue;
    private readonly VoiceOverlayCoordinator _overlay;
    private readonly ILogger<OpenMicCoordinator> _logger;

    private bool _wired;

    /// <summary>What the overlay shows while read-aloud is synthesizing (text-to-sound) but not yet playing a word.</summary>
    private const string PreparingStatus = "Preparing…";

    // Finished utterances wait here to be injected one at a time, in the order they were spoken. Without this,
    // each injection was fire-and-forget and its STT-cleanup call ran concurrently, so a shorter utterance's
    // faster cleanup could overtake a longer one spoken before it and land out of order.
    private readonly Channel<string> _injections =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Whether read-aloud is playing right now — the only time a loud microphone means anything to this coordinator.</summary>
    private bool _isPlaying;

    // Read when listening starts rather than per frame: a level fires many times a second, and the silence
    // timeout next to these is read once at the same point for the same reason.
    private bool _stopReadAloudWhenSpeaking;
    private double _stopReadAloudThreshold;

    // The most recent microphone level, so the barge-in check in HandleSpeechStarted can gate on loudness.
    private double _lastLevel;

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
        // dictation ever being enabled (the per-session toggle, the Options "Test" button), and its overlay must
        // still show. Barge-in (pausing the mic) is gated on actually listening inside the handler.
        _playbackQueue.PlaybackActiveChanged += _OnPlaybackActiveChanged;
        _playbackQueue.SpeakingStarted += _OnSpeakingStarted;

        // One consumer, drains the injection queue in order. Idle until an utterance arrives, so it costs nothing
        // when open-mic is off (and touches no dispatcher in tests, which never enqueue through it).
        _ = _ConsumeInjectionsAsync();
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

    // Pause the mic while read-aloud plays so it never transcribes the cockpit's own speech — UNLESS the operator
    // asked to interrupt by talking (AC-9), in which case the mic stays open so the VAD can actually hear them
    // (headphones assumed; the setting says so). Only while listening; the overlay is surfaced unconditionally.
    private void _OnPlaybackActiveChanged(object? sender, bool active)
    {
        if (IsListening && !_stopReadAloudWhenSpeaking)
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

    private void _OnSpeakingStarted(object? sender, EventArgs e) => Dispatcher.UIThread.Post(HandleSpeakingStarted);

    private void _OnSpeechStarted(object? sender, EventArgs e) => Dispatcher.UIThread.Post(HandleSpeechStarted);

    private void _OnSpeechEnded(object? sender, EventArgs e) => Dispatcher.UIThread.Post(HandleSpeechEnded);

    private void _OnAudioLevelSampled(object? sender, double level) => Dispatcher.UIThread.Post(() => HandleAudioLevel(level));

    /// <summary>
    /// Test seam: the VAD heard speech start. Open-mic listens the whole time it is on, so this — not
    /// <see cref="StartAsync"/> — is the moment there is something to show: a pill that appeared when you
    /// switched open-mic on and sat there would only be saying the feature is on.
    /// </summary>
    internal void HandleSpeechStarted()
    {
        // Barge-in (AC-9): real speech — the VAD said so, not just any noise — over read-aloud, loud enough to mean
        // it (the threshold, which the mic-level meter helps set), stops the read-aloud. Tying it to detected speech
        // rather than a raw level is why a cough or a door no longer cuts the cockpit off mid-sentence.
        if (_isPlaying && _stopReadAloudWhenSpeaking && _lastLevel >= _stopReadAloudThreshold)
        {
            _playbackQueue.StopAll();
        }

        _overlay.SetOpenMic(VoiceOverlayState.Listening);
    }

    /// <summary>Test seam: the utterance is over and about to be transcribed — the part worth a spinner.</summary>
    internal void HandleSpeechEnded() => _overlay.SetOpenMic(VoiceOverlayState.Transcribing);

    /// <summary>Test seam: read-aloud became active or went idle. Active means it is preparing (synthesizing, still silent) — <see cref="HandleSpeakingStarted"/> flips it to speaking once audio actually plays.</summary>
    internal void HandlePlaybackActiveChanged(bool active)
    {
        _isPlaying = active;
        _overlay.SetReadAloud(active ? VoiceOverlayState.Preparing : null, active ? PreparingStatus : null);
    }

    /// <summary>Test seam: the first synthesized clip started playing, so the overlay moves from "preparing" to "reading aloud".</summary>
    internal void HandleSpeakingStarted() => _overlay.SetReadAloud(VoiceOverlayState.Speaking);

    /// <summary>
    /// Test seam: one microphone level. Feeds the pill's waveform and remembers the latest level, which
    /// <see cref="HandleSpeechStarted"/> checks against the threshold — the barge-in stop fires on detected speech,
    /// not on this raw level, so a loud noise that is not speech no longer interrupts read-aloud (AC-9).
    /// </summary>
    internal void HandleAudioLevel(double level)
    {
        _lastLevel = level;
        _overlay.PushLevel(level);
    }

    // Queue rather than inject inline: the injection (STT cleanup + inject) is awaited one at a time by the
    // consumer, so utterances land in spoken order.
    private void _OnUtteranceTranscribed(object? sender, string rawText) => _injections.Writer.TryWrite(rawText);

    private async Task _ConsumeInjectionsAsync()
    {
        await foreach (var rawText in _injections.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            // Inject on the UI thread and wait for it to finish before taking the next, so a shorter utterance's
            // faster cleanup can never overtake a longer one spoken before it.
            var done = new TaskCompletionSource();
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await InjectUtteranceAsync(rawText);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Voice injection failed for a dictated utterance; skipping it.");
                }
                finally
                {
                    done.TrySetResult();
                }
            });

            await done.Task.ConfigureAwait(false);
        }
    }

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
            // Nothing to do without a session, or when the utterance filtered down to nothing (a throat-clear or a
            // bare "um" the STT noise filter removed) — injecting empty text, and auto-submitting it, is exactly
            // what "have a normal conversation" must not do.
            if (session is null || string.IsNullOrWhiteSpace(rawText))
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
