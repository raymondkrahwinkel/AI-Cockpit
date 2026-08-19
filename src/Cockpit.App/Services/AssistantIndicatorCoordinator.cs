using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Cockpit.App.Docking;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using Material.Icons;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.App.Services;

// Feeds the sidebar's `AssistantIndicatorViewModel` and acts on what the operator does to it
// (AC-543). The one place that answers the question the indicator exists to ask: *who* is listening.
// The indicator itself knows nothing about any of this — it is a reusable component (criterion 21) that AC-238
// will drop into the companion window, so the three sources that decide its state are joined here instead of
// reached for from inside it: the assistant host (ready/thinking/speaking/unavailable), open-mic (listening
// continuously), and dictation (`F9`, which is not the assistant at all and is the one state on this chip
// that means your words are going somewhere else).
//
// Dictation outranks everything else shown here, deliberately. The other states are about the assistant and are
// merely informative; this one is a warning that the microphone is pointed at a session, and a chip that showed
// "Ready" while F9 was recording would be wrong in the single way that costs something.
public sealed class AssistantIndicatorCoordinator : ISingletonService
{
    private readonly AssistantSessionHost _assistant;
    private readonly OpenMicCoordinator _openMic;
    private readonly VoiceOverlayCoordinator _overlay;
    private readonly IAssistantSettingsStore _settings;
    private readonly IVoicePlaybackQueue _playbackQueue;
    private readonly IAssistantSpawnAuditLog _spawnAuditLog;
    private readonly CockpitViewModel _cockpit;
    private readonly ILogger<AssistantIndicatorCoordinator> _logger;

    private readonly IDockPanelRegistry? _dockPanels;

    // AC-953: the assistant's id in the dock rail, persisted as `LayoutSettings.OpenDockPanelId`.
    public const string DockPanelId = "assistant";

    // The pop-out, kept between openings rather than rebuilt: closing it must not disturb the conversation behind it (criterion 7).
    // Null while the assistant is docked — there is no window then, which is what "geen ownerless venster" means.
    private AssistantChatWindow? _chatWindow;

    // The chat's view model — the standing holder across a host swap (AC-953), so docking and undocking keep the
    // same conversation, input text and attachments. Also how a settings change reaches an open chat without
    // touching the window off the UI thread.
    private AssistantChatViewModel? _chatViewModel;

    // Test seam: whether a floating window is standing right now. Null the moment one closes (see `_ShowChatWindow`),
    // so "two hosts at once" is exactly this being non-null while the rail also shows the chat — the state AC-953
    // exists to make impossible, and the one the headless harness cannot ask the platform about (it runs without an
    // application lifetime, so there is no window list to enumerate).
    internal AssistantChatWindow? OpenChatWindow => _chatWindow;

    public AssistantIndicatorCoordinator(
        AssistantSessionHost assistant,
        OpenMicCoordinator openMic,
        VoiceOverlayCoordinator overlay,
        IAssistantSettingsStore settings,
        IVoicePlaybackQueue playbackQueue,
        IAssistantSpawnAuditLog spawnAuditLog,
        CockpitViewModel cockpit,
        IDockPanelRegistry? dockPanels = null,
        ILogger<AssistantIndicatorCoordinator>? logger = null)
    {
        _assistant = assistant;
        _openMic = openMic;
        _overlay = overlay;
        _settings = settings;
        _playbackQueue = playbackQueue;
        _spawnAuditLog = spawnAuditLog;
        _cockpit = cockpit;
        _dockPanels = dockPanels;
        _logger = logger ?? NullLogger<AssistantIndicatorCoordinator>.Instance;
    }

    // The chip the sidebar binds to. One instance, fed from here.
    public AssistantIndicatorViewModel Indicator { get; } = new();

    // Subscribes to everything that can change what the chip says, and to what the operator does to it.
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

        // The microphone line on the chip. Same feed as the voice pill's waveform — all three capture sources
        // already funnel through the overlay coordinator, and the chip drops what arrives in a state that has no
        // microphone, so this needs no filtering of its own.
        _overlay.LevelSampled += _OnLevelSampled;

        Indicator.Clicked += (_, _) => _ = _OpenChatAsync();
        Indicator.ListeningModeSelected += (_, mode) => _ = _ApplyListeningModeAsync(mode);

        // AC-953: the assistant becomes a real dock panel, replacing AC-951's placeholder. Its registration follows
        // the dock stand rather than standing permanently — undocked, the assistant is a window, and a rail tab for
        // it would be a second one waiting to be opened. Driven off the property rather than only from the swap,
        // because the stand also arrives from the layout restore, well after this runs.
        _cockpit.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CockpitViewModel.AssistantDocked))
            {
                _ApplyDockRegistration();
            }
        };

        _ApplyDockRegistration();

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
        Indicator.IsConsentBypassActive = settings.HasConsentBypass;
        Indicator.IsFeatureEnabled = settings.IsEnabled;
    }

    // Re-reads whether the feature is on, so switching it in Options adds or removes the chip without a restart.
    public async Task ApplySettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(true);
        Indicator.IsFeatureEnabled = settings.IsEnabled;
        Indicator.IsConsentBypassActive = settings.HasConsentBypass;
        _Refresh();

        // The pop-out too, when it is open. It is kept between openings and is ownerless, so Options can be used
        // while it sits there — and Show() on a live window raises no new Opened, which means its own open-time read
        // would never run again. Without this the chip refreshed its bypass mark and the window behind it did not.
        // Held as its own field rather than read back off Window.DataContext: this runs on whatever thread the
        // Options save left it on, and an Avalonia property read off the UI thread throws.
        if (_chatViewModel is { } chat)
        {
            await chat.ApplySettingsAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    // Mirrors the sidebar's collapsed state onto the chip, which drops to its bare badge for the rail.
    public void SetCollapsed(bool collapsed) => Indicator.IsCollapsed = collapsed;

    private void _OnSourceChanged(object? sender, PropertyChangedEventArgs e) =>
        Dispatcher.UIThread.Post(_Refresh);

    // Capture runs off the UI thread, and the chip's line is a bound collection — marshalled like every other source.
    private void _OnLevelSampled(object? sender, double level) =>
        Dispatcher.UIThread.Post(() => Indicator.PushLevel(level));

    // Whether the playback queue is speaking right now — the chip's `AssistantActivity.Speaking`.
    // Kept as a field rather than asked of the queue in `_ResolveActivity`, because the queue reports
    // this by event and has no property to read back — and the event arrives on the playback thread, so the value
    // is captured here and the refresh marshalled like every other source.
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
        Indicator.PreparationStatus = _assistant.PreparationStatus;
        Indicator.PreparationProgress = _assistant.PreparationProgress;

        // Derived, not stored: what "always on" means is that the microphone is open, and that is already one
        // persisted flag on the open-mic coordinator. A second copy here would be a second thing to keep in step.
        Indicator.ListeningMode = _openMic.IsListening ? AssistantListeningMode.AlwaysOn : AssistantListeningMode.Off;
    }

    // What the chip shows, in priority order. Dictation first — see the remarks on this class for why the one
    // state that is not about the assistant is the one that wins.
    private AssistantActivity _ResolveActivity()
    {
        if (_overlay.Overlay.State is VoiceOverlayState.Listening or VoiceOverlayState.Transcribing
            && !_openMic.IsListening
            && _assistant.Activity is not (AssistantActivity.Listening or AssistantActivity.Thinking
                or AssistantActivity.Transcribing or AssistantActivity.Preparing))
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

    // The chip is clicked: bring the assistant up if this is the first time, and show the conversation. Equal to
    // holding the hotkey as far as starting goes — a voice feature reachable only by a key shuts people out.
    // Fire-and-forget from the click handler below, so a failure here used to vanish silently (AC-765) — caught
    // and logged instead, rather than leaving the operator clicking a button that does nothing.
    private async Task _OpenChatAsync()
    {
        try
        {
            // AC-953: which host the chat opens in is the stand the operator last left it in, and it survives a
            // restart because it is read back off `LayoutSettings`.
            await _ShowInAsync(_cockpit.AssistantDocked).ConfigureAwait(true);

            // The window first, the session after — AC-959. Starting it is not a fast local call: it resolves the
            // MCP catalog, stands up loopback endpoints, renews OAuth sign-ins *over the network*, spawns the CLI
            // and replays the transcript. Measured on one open: 3,4 seconds from the start of that to the spawned
            // process, with a Depot sign-in that failed quickly — a slow one costs more. Awaiting it before the
            // window meant several seconds of nothing at all between the click and anything appearing.
            //
            // The window copes with a session that is not up: it binds to the host and shows "the session has not
            // started yet" until one arrives, which is exactly what an operator wants to see while it starts. The
            // await stays, so a failure still reaches the catch below rather than becoming an unobserved task.
            await _assistant.EnsureStartedAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Opening the assistant chat window failed.");
        }
    }

    // The cockpit's own window closed, so the pop-out onto it goes too — see `_ShowChatWindow` for why by hand.
    private void _OnMainWindowClosed(object? sender, EventArgs e) => _chatWindow?.Close();

    // The one chat view model, built on first need and kept afterwards: it is what carries the conversation, the
    // typed-but-unsent text, the attachments and (AC-953) the scroll position across a host swap.
    private AssistantChatViewModel _EnsureChatViewModel()
    {
        if (_chatViewModel is { } existing)
        {
            return existing;
        }

        var chat = new AssistantChatViewModel(_assistant, _settings, _playbackQueue, _spawnAuditLog, Indicator, cockpit: _cockpit);
        chat.DockToggleRequested += (_, _) => _ = _ToggleDockAsync();
        _chatViewModel = chat;
        return chat;
    }

    // Whether the rail offers an Assistant tab at all: only while the assistant actually stands there.
    private void _ApplyDockRegistration()
    {
        if (_dockPanels is not { } panels)
        {
            return;
        }

        if (_cockpit.AssistantDocked)
        {
            panels.Register(new DockPanelRegistration(
                DockPanelId,
                "Assistant",
                MaterialIconKind.Creation,
                _CreateDockedChatView));

            return;
        }

        panels.Unregister(DockPanelId);

        // A tab that is gone cannot leave its panel open behind it — that would hold the rail expanded onto a
        // registration the rail can no longer resolve, which draws as an empty rail beside the floating window.
        if (_cockpit.OpenDockPanelId == DockPanelId)
        {
            _cockpit.OpenDockPanelId = null;
        }
    }

    // The rail is the only caller of this factory, so being built by it *is* being docked. Two routes reach it
    // without going through `_ShowInAsync` — clicking the rail tab, and the restore that reopens the last open
    // panel at startup — so the same handover has to happen here, or those two leave the window standing beside
    // the docked view with the operator typing into whichever one they clicked last.
    //
    // This is also exactly the right moment for it: the factory runs after the rail has decided to show the chat
    // and before the view it returns is attached, so the window's view detaches — handing over its scroll
    // position — before this one reads it.
    private Control _CreateDockedChatView()
    {
        var chat = _EnsureChatViewModel();
        chat.IsDocked = true;
        _chatWindow?.Close();

        return new AssistantChatView { DataContext = chat };
    }

    private void _ShowChatWindow()
    {
        if (_chatWindow is null)
        {
            _chatWindow = new AssistantChatWindow { DataContext = _EnsureChatViewModel() };

            // Dropped on close so the next click builds a fresh window — but nothing about the session is touched
            // here, which is the whole of criterion 7: the window is a peephole, not the owner. The view model goes
            // with it only when this was a real close: docking closes this window too, and there the rail takes it over.
            _chatWindow.Closed += (_, _) =>
            {
                _chatWindow = null;
                if (_chatViewModel is { IsDocked: false })
                {
                    _chatViewModel = null;
                }
            };

            // Shown without an owner, and closed with the cockpit by hand instead. Ownerless is deliberate: an owned
            // window minimises and restores with its owner, and this one has to stay reachable while the cockpit is
            // in the background — that is the whole point of a global hotkey. But Avalonia's default shutdown is
            // "when the last window closes", so an ownerless window that outlives the main one keeps the entire
            // process alive: the cockpit vanished from the screen, the chat pop-out stayed sitting there, and the
            // app went on running with its global hotkeys still registered — which is what then refused F10 to the
            // next launch, since the key was still held by a process nobody could see.
            // Docked, neither half applies: the view sits inside MainWindow, so it closes with it by itself.
            if (Avalonia.Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            {
                main.Closed += _OnMainWindowClosed;
                _chatWindow.Closed += (_, _) => main.Closed -= _OnMainWindowClosed;
            }
        }

        WindowActivation.BringToFront(_chatWindow);
    }

    // Puts the chat in one host and takes it out of the other — the single place that decides where it stands, so
    // there is no route that can leave two of them on screen at once. Every caller says which host it wants
    // rather than what to change, which makes it idempotent: asking for the host it is already in does nothing.
    //
    // The order inside each branch is the whole of AC-953: the old host is torn down first and the new one built
    // after, so the leaving view has written its scroll position onto the view model before the arriving view
    // reads it. Build-then-tear-down would have the new view read a stale position and the old view overwrite it
    // afterwards with nothing looking.
    private async Task _ShowInAsync(bool docked)
    {
        if (_chatViewModel is { } chat)
        {
            chat.IsDocked = docked;
        }

        if (docked)
        {
            _chatWindow?.Close();

            if (!_cockpit.AssistantDocked || _cockpit.OpenDockPanelId != DockPanelId)
            {
                await _cockpit.SetAssistantDockedAsync(true, DockPanelId).ConfigureAwait(true);
            }

            // Docked, "show the chat" also means putting the cockpit in front of whatever the operator was
            // looking at — the floating window's own Show + BringToFront, on the window it lives in now.
            if (Avalonia.Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            {
                WindowActivation.BringToFront(main);
            }

            return;
        }

        // Closing the panel is what detaches the docked view, and that is what hands its scroll position to the
        // one the window is about to build.
        if (_cockpit.AssistantDocked || _cockpit.OpenDockPanelId == DockPanelId)
        {
            await _cockpit.SetAssistantDockedAsync(false, null).ConfigureAwait(true);
        }

        _ShowChatWindow();
    }

    // The header's Dock/Undock button: the other host, whichever this is.
    private async Task _ToggleDockAsync()
    {
        if (_chatViewModel is { } chat)
        {
            await _ShowInAsync(!chat.IsDocked).ConfigureAwait(true);
        }
    }

    // Switches the microphone between held-only and held-open — the only two modes the chip offers.
    // The wake-word mode is refused rather than absent from this check: the enum still carries it (the wake word
    // is its own future ticket), and a mode that cannot be picked today is one a caller could still pass
    // tomorrow by reading the enum rather than the UI. Guarding here costs one line and means the microphone
    // never opens on a filter that does not exist.
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
