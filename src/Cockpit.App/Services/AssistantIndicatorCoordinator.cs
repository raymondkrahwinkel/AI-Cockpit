using System.ComponentModel;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;

namespace Cockpit.App.Services;

/// <summary>
/// Feeds the sidebar's <see cref="AssistantIndicatorViewModel"/> and acts on what the operator does to it
/// (AC-543). The one place that answers the question the indicator exists to ask: <em>who</em> is listening.
/// </summary>
/// <remarks>
/// The indicator itself knows nothing about any of this — it is a reusable component (criterion 21) that AC-238
/// will drop into the companion window, so the three sources that decide its state are joined here instead of
/// reached for from inside it: the assistant host (ready/thinking/speaking/unavailable), open-mic (listening
/// continuously), and dictation (<c>F9</c>, which is not the assistant at all and is the one state on this chip
/// that means your words are going somewhere else).
/// <para>
/// Dictation outranks everything else shown here, deliberately. The other states are about the assistant and are
/// merely informative; this one is a warning that the microphone is pointed at a session, and a chip that showed
/// "Ready" while F9 was recording would be wrong in the single way that costs something.
/// </para>
/// </remarks>
public sealed class AssistantIndicatorCoordinator : ISingletonService
{
    private readonly AssistantSessionHost _assistant;
    private readonly OpenMicCoordinator _openMic;
    private readonly VoiceOverlayCoordinator _overlay;
    private readonly IAssistantSettingsStore _settings;
    private readonly IVoicePlaybackQueue _playbackQueue;
    private readonly IAssistantSpawnAuditLog _spawnAuditLog;

    /// <summary>The pop-out, kept between openings rather than rebuilt: closing it must not disturb the conversation behind it (criterion 7).</summary>
    private AssistantChatWindow? _chatWindow;

    public AssistantIndicatorCoordinator(
        AssistantSessionHost assistant,
        OpenMicCoordinator openMic,
        VoiceOverlayCoordinator overlay,
        IAssistantSettingsStore settings,
        IVoicePlaybackQueue playbackQueue,
        IAssistantSpawnAuditLog spawnAuditLog)
    {
        _assistant = assistant;
        _openMic = openMic;
        _overlay = overlay;
        _settings = settings;
        _playbackQueue = playbackQueue;
        _spawnAuditLog = spawnAuditLog;
    }

    /// <summary>The chip the sidebar binds to. One instance, fed from here.</summary>
    public AssistantIndicatorViewModel Indicator { get; } = new();

    /// <summary>Subscribes to everything that can change what the chip says, and to what the operator does to it.</summary>
    public void Start()
    {
        _assistant.PropertyChanged += _OnSourceChanged;
        _openMic.PropertyChanged += _OnSourceChanged;
        _overlay.Overlay.PropertyChanged += _OnSourceChanged;

        // The one source the chip was declared to have and never actually listened to. AssistantActivity.Speaking
        // exists, the indicator renders it, and there is even a baseline for that frame — but nothing ever set it,
        // because the assistant host only ever writes Activity for things it does itself (a hold, a send, a start)
        // and speaking is something the playback queue does afterwards. So the one state the operator most wants
        // at a glance — "it is talking to me right now" — was dead from the day it was drawn.
        _playbackQueue.PlaybackActiveChanged += _OnPlaybackActiveChanged;

        Indicator.Clicked += (_, _) => _ = _OpenChatAsync();
        Indicator.ListeningModeSelected += (_, mode) => _ = _ApplyListeningModeAsync(mode);

        // The chip owns the one-time cost explanation (criterion 18); this only tells it whether the operator has
        // already been given it, and writes back once they have — so it stays given across restarts rather than
        // returning on the next launch, which is the shape of warning people learn to click through.
        _ = _SeedAcknowledgementAsync();

        _Refresh();
    }

    private async Task _SeedAcknowledgementAsync()
    {
        var settings = await _settings.LoadAsync().ConfigureAwait(true);
        Indicator.AlwaysOnCostAcknowledged = settings.AlwaysOnCostAcknowledged;
        Indicator.IsFeatureEnabled = settings.IsEnabled;
    }

    /// <summary>Re-reads whether the feature is on, so switching it in Options adds or removes the chip without a restart.</summary>
    public async Task ApplySettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(true);
        Indicator.IsFeatureEnabled = settings.IsEnabled;
        _Refresh();
    }

    /// <summary>Mirrors the sidebar's collapsed state onto the chip, which drops to its bare badge for the rail.</summary>
    public void SetCollapsed(bool collapsed) => Indicator.IsCollapsed = collapsed;

    private void _OnSourceChanged(object? sender, PropertyChangedEventArgs e) =>
        Dispatcher.UIThread.Post(_Refresh);

    /// <summary>Whether the playback queue is speaking right now — the chip's <see cref="AssistantActivity.Speaking"/>.</summary>
    /// <remarks>
    /// Kept as a field rather than asked of the queue in <see cref="_ResolveActivity"/>, because the queue reports
    /// this by event and has no property to read back — and the event arrives on the playback thread, so the value
    /// is captured here and the refresh marshalled like every other source.
    /// </remarks>
    private bool _isSpeaking;

    private void _OnPlaybackActiveChanged(object? sender, bool active) =>
        Dispatcher.UIThread.Post(() =>
        {
            _isSpeaking = active;
            _Refresh();
        });

    private void _Refresh()
    {
        Indicator.Activity = _ResolveActivity();
        Indicator.UnavailableReason = _assistant.UnavailableReason;
        Indicator.ProfileLabel = _assistant.ProfileLabel;

        // Derived, not stored: what "always on" means is that the microphone is open, and that is already one
        // persisted flag on the open-mic coordinator. A second copy here would be a second thing to keep in step.
        Indicator.ListeningMode = _openMic.IsListening ? AssistantListeningMode.AlwaysOn : AssistantListeningMode.Off;
    }

    /// <summary>
    /// What the chip shows, in priority order. Dictation first — see the remarks on this class for why the one
    /// state that is not about the assistant is the one that wins.
    /// </summary>
    private AssistantActivity _ResolveActivity()
    {
        if (_overlay.Overlay.State is VoiceOverlayState.Listening or VoiceOverlayState.Transcribing
            && !_openMic.IsListening
            && _assistant.Activity is not (AssistantActivity.Listening or AssistantActivity.Thinking))
        {
            return AssistantActivity.Dictating;
        }

        // Speaking, before the open-mic stand below and after dictation above. It outranks "listening
        // continuously" because it is a handling and that is a stand: with the microphone open the assistant is
        // always, in some sense, listening, and saying so while it is audibly talking answers the wrong question.
        // It does not outrank a held key or a hold being transcribed — those are the operator interrupting, and
        // barge-in stops the playback anyway, so reporting Speaking there would be a frame of the state that is
        // just ending.
        if (_isSpeaking && _assistant.Activity is AssistantActivity.Ready or AssistantActivity.Thinking)
        {
            return AssistantActivity.Speaking;
        }

        // The standing state beats the host's momentary one: with the microphone held open, "listening
        // continuously" is what is true between utterances, and it is a stand rather than a handling.
        if (_openMic.IsListening && _assistant.Activity is AssistantActivity.Ready or AssistantActivity.Listening)
        {
            return AssistantActivity.ListeningContinuously;
        }

        return _assistant.Activity;
    }

    /// <summary>
    /// The chip is clicked: bring the assistant up if this is the first time, and show the conversation. Equal to
    /// holding the hotkey as far as starting goes — a voice feature reachable only by a key shuts people out.
    /// </summary>
    private async Task _OpenChatAsync()
    {
        await _assistant.EnsureStartedAsync().ConfigureAwait(true);

        if (_chatWindow is null)
        {
            _chatWindow = new AssistantChatWindow
            {
                DataContext = new AssistantChatViewModel(_assistant, _settings, _playbackQueue, _spawnAuditLog),
            };

            // Dropped on close so the next click builds a fresh window — but nothing about the session is touched
            // here, which is the whole of criterion 7: the window is a peephole, not the owner.
            _chatWindow.Closed += (_, _) => _chatWindow = null;

            // Shown without an owner, and closed with the cockpit by hand instead. Ownerless is deliberate: an owned
            // window minimises and restores with its owner, and this one has to stay reachable while the cockpit is
            // in the background — that is the whole point of a global hotkey. But Avalonia's default shutdown is
            // "when the last window closes", so an ownerless window that outlives the main one keeps the entire
            // process alive: the cockpit vanished from the screen, the chat pop-out stayed sitting there, and the
            // app went on running with its global hotkeys still registered — which is what then refused F10 to the
            // next launch, since the key was still held by a process nobody could see.
            if (Avalonia.Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            {
                main.Closed += _OnMainWindowClosed;
                _chatWindow.Closed += (_, _) => main.Closed -= _OnMainWindowClosed;
            }
        }

        _chatWindow.Show();
        _chatWindow.Activate();
    }

    /// <summary>The cockpit's own window closed, so the pop-out onto it goes too — see <see cref="_OpenChatAsync"/> for why by hand.</summary>
    private void _OnMainWindowClosed(object? sender, EventArgs e) => _chatWindow?.Close();

    /// <summary>
    /// Switches the microphone between held-only and held-open — the only two modes the chip offers.
    /// </summary>
    /// <remarks>
    /// The wake-word mode is refused rather than absent from this check: the enum still carries it (the wake word
    /// is its own future ticket), and a mode that cannot be picked today is one a caller could still pass
    /// tomorrow by reading the enum rather than the UI. Guarding here costs one line and means the microphone
    /// never opens on a filter that does not exist.
    /// </remarks>
    private async Task _ApplyListeningModeAsync(AssistantListeningMode mode)
    {
        if (mode == AssistantListeningMode.AlwaysOnWithWakeWord)
        {
            return;
        }

        if (_openMic.IsListening != (mode == AssistantListeningMode.AlwaysOn))
        {
            await _openMic.ToggleOpenMicCommand.ExecuteAsync(null).ConfigureAwait(true);
        }

        // The chip already showed the explanation and set this before raising the event; persisting it here is
        // what stops it returning after a restart.
        if (Indicator.AlwaysOnCostAcknowledged)
        {
            var settings = await _settings.LoadAsync().ConfigureAwait(true);
            if (!settings.AlwaysOnCostAcknowledged)
            {
                await _settings.SaveAsync(settings with { AlwaysOnCostAcknowledged = true }).ConfigureAwait(true);
            }
        }

        _Refresh();
    }
}
