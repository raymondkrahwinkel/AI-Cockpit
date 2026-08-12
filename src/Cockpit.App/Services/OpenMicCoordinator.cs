using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.App.Services;

// Keeps the microphone open for the assistant and exposes the on/off behind the indicator's listening mode: while
// listening, the continuous `IOpenMicListener` hands each finished utterance to
// `AssistantSessionHost`, and the mic pauses while read-aloud is playing so it never transcribes the
// cockpit's own speech. The on/off state is persisted, so it resumes next launch.
// *It used to dictate.* Until AC-543 this injected into whichever session happened to be selected, which
// made "leave the microphone open" mean something different depending on where you last clicked. It now always
// means the assistant; `F9` is the only path into a session and is unchanged. What follows from that, and is
// worth saying out loud: with this on, everything you say reaches the assistant — an aside to a colleague, a
// phone call, thinking out loud — and each of those costs a turn. A wake word is the filter for that, and it is
// not built yet, so the indicator says so at the moment it is switched on.
// Threading mirrors `VoicePushToTalkCoordinator`: `IOpenMicListener.UtteranceTranscribed`
// fires on the capture thread, so injection is marshaled onto the UI thread via
// `Dispatcher.UIThread`. `InjectUtteranceAsync` is the (UI-thread) logic the tests
// drive directly, since pumping a real Avalonia dispatcher loop from a unit test is not practical.
public sealed partial class OpenMicCoordinator : ObservableObject, ISingletonService, IOpenMicState
{
    private readonly IOpenMicListener _listener;
    private readonly IAssistantSessionHost _assistant;
    private readonly IVoiceSettingsStore _voiceSettingsStore;
    private readonly IAssistantSettingsStore _assistantSettingsStore;
    private readonly IVoicePlaybackQueue _playbackQueue;
    private readonly VoiceOverlayCoordinator _overlay;
    private readonly ILogger<OpenMicCoordinator> _logger;

    private bool _wired;

    // AC-628: `IsListening` is set two awaits after the guard reads it, so enabling and disabling are serialized here.
    private readonly SemaphoreSlim _enableGate = new(1, 1);

    // What the overlay shows while read-aloud is synthesizing (text-to-sound) but not yet playing a word.
    private const string PreparingStatus = "Preparing…";

    // Finished utterances wait here to be injected one at a time, in the order they were spoken. Without this,
    // each injection was fire-and-forget, so a shorter utterance's faster send could overtake a longer one
    // spoken before it and land out of order.
    private readonly Channel<string> _injections =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    // Whether read-aloud is playing right now — the only time a loud microphone means anything to this coordinator.
    private bool _isPlaying;

    // True while a push-to-talk hold has the microphone (AC-627). Volatile because the hold sets it on the UI
    // thread and the capture thread reads it in `_OnUtteranceTranscribed`.
    private volatile bool _suspendedForHold;

    // Read when listening starts rather than per frame: a level fires many times a second, and the silence
    // timeout next to these is read once at the same point for the same reason.
    private bool _stopReadAloudWhenSpeaking;
    private double _stopReadAloudThreshold;

    // The most recent microphone level, so the barge-in check in HandleSpeechStarted can gate on loudness.
    private double _lastLevel;

    public OpenMicCoordinator(
        IOpenMicListener listener,
        IAssistantSessionHost assistant,
        IVoiceSettingsStore voiceSettingsStore,
        IAssistantSettingsStore assistantSettingsStore,
        IVoicePlaybackQueue playbackQueue,
        VoiceOverlayCoordinator overlay,
        ILogger<OpenMicCoordinator> logger)
    {
        _listener = listener;
        _assistant = assistant;
        _voiceSettingsStore = voiceSettingsStore;
        _assistantSettingsStore = assistantSettingsStore;
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

    // True once voice is enabled — open-mic needs the mic pipeline, so the toggle is disabled until then.
    [ObservableProperty]
    private bool _isAvailable;

    // True while the mic is actively listening; drives the toggle button's on/off state.
    [ObservableProperty]
    private bool _isListening;

    // Reads settings at startup and resumes listening if it was left on; runtime toggling is via
    // `ToggleOpenMicCommand`. No-op unless both voice and the assistant are switched on.
    // Never throws, for the reason `VoicePushToTalkCoordinator.StartAsync` does not: its one caller
    // discards the task, so anything thrown here lands on a task nobody observes. It still cannot start when the
    // settings will not read or the microphone will not open — it says which now.
    //
    // Gated on *both* switches since AC-543. Voice, because that is the microphone pipeline; and the
    // assistant, because it is now the only destination — leaving the microphone open for a feature that is
    // switched off would record the room and send it nowhere.
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await _voiceSettingsStore.LoadAsync(cancellationToken);
            var assistant = await _assistantSettingsStore.LoadAsync(cancellationToken);
            IsAvailable = settings.IsEnabled && assistant.IsEnabled;
            if (IsAvailable && settings.OpenMicEnabled)
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

    // Re-reads both switches and closes the microphone if the assistant was just turned off. Called when the
    // Options page saves.
    // The gate in `StartAsync` only runs at launch, so switching the assistant off while open-mic
    // was listening left the microphone open with nowhere to send what it heard — every utterance was
    // transcribed and then silently dropped by a host that is off. An open microphone recording a room for no
    // reason is the one failure here worth closing without being asked to.
    public async Task ApplyAssistantSettingsAsync(CancellationToken cancellationToken = default)
    {
        var voice = await _voiceSettingsStore.LoadAsync(cancellationToken);
        var assistant = await _assistantSettingsStore.LoadAsync(cancellationToken);

        IsAvailable = voice.IsEnabled && assistant.IsEnabled;
        if (!IsAvailable && IsListening)
        {
            await _DisableAsync();
        }
    }

    // Runtime on/off, gated on voice being enabled; persists the state so it is remembered next launch.
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

    // AC-628: `caller` is there so a duplicate enable names the path it came in on rather than arriving anonymously.
    private async Task _EnableAsync(CancellationToken cancellationToken = default, [CallerMemberName] string caller = "")
    {
        await _enableGate.WaitAsync(cancellationToken);
        try
        {
            if (IsListening)
            {
                _logger.LogInformation("Open-mic is already listening; the request from {Caller} was ignored.", caller);
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
        finally
        {
            _enableGate.Release();
        }
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
        await _enableGate.WaitAsync();
        try
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
        finally
        {
            _enableGate.Release();
        }
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
            else if (!_suspendedForHold)
            {
                // Read-aloud finishing is not this microphone's cue to come back while a hold still has it.
                _listener.Resume();
            }
        }

        var source = _playbackQueue.ActiveSource;
        Dispatcher.UIThread.Post(() => HandlePlaybackActiveChanged(active, source));
    }

    // AC-627: the hold wins over open-mic and takes the microphone for its duration.
    public IDisposable SuspendForHold()
    {
        if (!IsListening)
        {
            // Nothing to step aside from. Still a handle, so the caller's dispose is unconditional.
            return new HoldSuspension(this);
        }

        _suspendedForHold = true;
        _listener.Pause();

        // Pause drops the half-formed utterance; these are the finished ones still queued, spoken before the key
        // went down and so the composer's rather than the assistant's (AC-627).
        while (_injections.Reader.TryRead(out _))
        {
        }

        // The pill was showing open-mic's own state; the hold is about to claim it.
        _overlay.SetOpenMic(null);

        return new HoldSuspension(this);
    }

    private void _ResumeAfterHold()
    {
        if (!_suspendedForHold)
        {
            return;
        }

        _suspendedForHold = false;

        // Unless read-aloud still has it paused for barge-in (AC-9) — the hold ending does not undo that.
        if (!_isPlaying || _stopReadAloudWhenSpeaking)
        {
            _listener.Resume();
        }
    }

    private sealed class HoldSuspension(OpenMicCoordinator owner) : IDisposable
    {
        public void Dispose() => owner._ResumeAfterHold();
    }

    private void _OnSpeakingStarted(object? sender, EventArgs e)
    {
        var source = _playbackQueue.ActiveSource;
        Dispatcher.UIThread.Post(() => HandleSpeakingStarted(source));
    }

    private void _OnSpeechStarted(object? sender, EventArgs e) => Dispatcher.UIThread.Post(HandleSpeechStarted);

    private void _OnSpeechEnded(object? sender, EventArgs e) => Dispatcher.UIThread.Post(HandleSpeechEnded);

    private void _OnAudioLevelSampled(object? sender, double level) => Dispatcher.UIThread.Post(() => HandleAudioLevel(level));

    // Test seam: the VAD heard speech start. Open-mic listens the whole time it is on, so this — not
    // `StartAsync` — is the moment there is something to show: a pill that appeared when you
    // switched open-mic on and sat there would only be saying the feature is on.
    internal void HandleSpeechStarted()
    {
        // Barge-in (AC-9): real speech — the VAD said so, not just any noise — over read-aloud, loud enough to mean
        // it (the threshold, which the mic-level meter helps set), stops the read-aloud. Tying it to detected speech
        // rather than a raw level is why a cough or a door no longer cuts the cockpit off mid-sentence.
        if (_isPlaying && _stopReadAloudWhenSpeaking && _lastLevel >= _stopReadAloudThreshold)
        {
            _playbackQueue.StopAll();
        }

        // Not the pill: an open microphone is the assistant's, and the chip already stands at "listening
        // continuously" for as long as it is open (Raymond, 2026-08-08 — the assistant's states left the pill).
        _assistant.ReportTranscribing(false);
    }

    // Test seam: the utterance is over and about to be transcribed — the part worth saying out loud, because it is
    // the gap between you stopping and the assistant starting.
    internal void HandleSpeechEnded() => _assistant.ReportTranscribing(true);

    // Test seam: read-aloud became active or went idle. Active means it is preparing (synthesizing, still silent) — `HandleSpeakingStarted` flips it to speaking once audio actually plays.
    //
    // AC-729: the assistant's own reply no longer reaches the pill — the chip already carries its Speaking state
    // (AssistantIndicatorCoordinator), so writing it here too would say the same thing twice, same reasoning as
    // AC-697's hold-flow. `_isPlaying` still tracks every source unconditionally: a barge-in has to be able to
    // stop the assistant reading its own answer, not just an ordinary session's.
    internal void HandlePlaybackActiveChanged(bool active, VoicePlaybackSource source = VoicePlaybackSource.Session)
    {
        _isPlaying = active;

        if (source == VoicePlaybackSource.Assistant)
        {
            return;
        }

        _overlay.SetReadAloud(active ? VoiceOverlayState.Preparing : null, active ? PreparingStatus : null);
    }

    // Test seam: the first synthesized clip started playing, so the overlay moves from "preparing" to "reading aloud".
    internal void HandleSpeakingStarted(VoicePlaybackSource source = VoicePlaybackSource.Session)
    {
        if (source == VoicePlaybackSource.Assistant)
        {
            return;
        }

        _overlay.SetReadAloud(VoiceOverlayState.Speaking);
    }

    // Test seam: one microphone level. Feeds the pill's waveform and remembers the latest level, which
    // `HandleSpeechStarted` checks against the threshold — the barge-in stop fires on detected speech,
    // not on this raw level, so a loud noise that is not speech no longer interrupts read-aloud (AC-9).
    internal void HandleAudioLevel(double level)
    {
        _lastLevel = level;
        _overlay.PushLevel(level);
    }

    // Queue rather than inject inline: the injection is awaited one at a time by the consumer, so utterances
    // land in spoken order.
    // Nothing is queued while a hold has the microphone: `Pause` cannot stop an utterance already inside the
    // transcribe call, which arrives here after the key went down (AC-627).
    private void _OnUtteranceTranscribed(object? sender, string rawText)
    {
        if (_suspendedForHold)
        {
            return;
        }

        _injections.Writer.TryWrite(rawText);
    }

    private async Task _ConsumeInjectionsAsync()
    {
        await foreach (var rawText in _injections.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            // Inject on the UI thread and wait for it to finish before taking the next, so a shorter utterance's
            // faster send can never overtake a longer one spoken before it.
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

    // Test seam: the UI-thread logic that hands one finished utterance to the assistant.
    // The pill is released here rather than on `SpeechEnded`: sending runs between the two, and a spinner
    // that stops before the text lands would be a spinner that lied about the last part of the wait. Released
    // in a finally — an utterance that fails to send still ends, and the alternative is a pill spinning over a
    // sentence that is never coming.
    internal async Task InjectUtteranceAsync(string rawText)
    {
        try
        {
            // Nothing to do when the utterance filtered down to nothing (a throat-clear or a bare "um" the STT
            // noise filter removed) — sending empty text is exactly what "have a normal conversation" must not do.
            // Nor while a hold has the microphone: the consumer may have taken this one out of the queue before the
            // key went down, and this is the last point it can still be dropped (AC-627).
            if (string.IsNullOrWhiteSpace(rawText) || _suspendedForHold)
            {
                return;
            }

            // Straight to the assistant, raw (AC-543 criterion 20). Two things changed here at once and both are
            // deliberate. The destination: open-mic used to dictate into whichever session happened to be
            // selected, which made "leave the microphone open" mean something different depending on where you
            // last clicked; it now always means the assistant, and F9 is left as the only way to dictate into a
            // session. And the cleanup pass is gone: what Whisper heard is what the assistant gets (decision 10),
            // because the tidying it did is the system prompt's job now.
            await _assistant.SendAsync(rawText);
        }
        finally
        {
            _overlay.SetOpenMic(null);
        }
    }

    partial void OnIsAvailableChanged(bool value) => ToggleOpenMicCommand.NotifyCanExecuteChanged();
}
