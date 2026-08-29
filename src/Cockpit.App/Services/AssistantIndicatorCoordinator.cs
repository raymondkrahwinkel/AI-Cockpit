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
using Cockpit.Plugins.Abstractions.Docking;
using Material.Icons;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.App.Services;

// AC-543: feeds the sidebar's `AssistantIndicatorViewModel`, joining the three sources that decide its
// state (assistant host, open-mic, F9 dictation) so the reusable indicator (AC-238) stays ignorant of them.
// Dictation outranks the rest: "Ready" showing while F9 records would be wrong in the way that costs.
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
    // Null while the assistant is docked — there is no window then.
    private AssistantChatWindow? _chatWindow;

    // The chat's view model — the standing holder across a host swap (AC-953), so docking and undocking
    // keep the same conversation, input text and attachments.
    private AssistantChatViewModel? _chatViewModel;

    // Test seam: whether a floating window is standing right now. Null the moment one closes — the headless
    // harness has no application lifetime to enumerate windows from, so it needs this instead.
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

        // AC-1013: AssistantActivity.Speaking was rendered but never set — the host only writes Activity for
        // things it does itself, not for playback afterwards — so "it is talking" was dead until wired here.
        _playbackQueue.PlaybackActiveChanged += _OnPlaybackActiveChanged;

        // Same feed as the voice pill's waveform; all three capture sources already funnel through the
        // overlay coordinator, so no extra filtering is needed here.
        _overlay.LevelSampled += _OnLevelSampled;

        Indicator.Clicked += (_, _) => _ = _OpenChatAsync();
        Indicator.ListeningModeSelected += (_, mode) => _ = _ApplyListeningModeAsync(mode);

        // AC-953: dock-panel registration follows the dock stand rather than standing permanently, driven off
        // the property (not just the swap) because the stand also arrives later, from the layout restore.
        _cockpit.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CockpitViewModel.AssistantDocked))
            {
                _ApplyDockRegistration();
            }
        };

        _ApplyDockRegistration();

        // The chip owns the one-time cost explanation (criterion 18); this only tracks whether the operator
        // has been given it and persists that, so it doesn't return on next launch (criterion 18).
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

        // AC-1013: also applies to the pop-out when open — it's ownerless and kept between openings, and Show()
        // on a live window raises no new Opened, so without this the window's bypass mark would go stale.
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

    // Whether the playback queue is speaking right now. Kept as a field rather than asked of the queue in
    // `_ResolveActivity`, because the queue reports this by event, with no property to read back.
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

        // Speaking outranks "listening continuously" — with the mic open the assistant is always in some sense
        // listening, so showing that while it audibly talks answers the wrong question. It does not outrank a
        // held key or a hold being transcribed: those are the operator interrupting, and barge-in stops playback.
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

    // The chip is clicked: bring the assistant up if this is the first time, and show the conversation — equal
    // to holding the hotkey, since a voice feature reachable only by a key shuts people out. Fire-and-forget
    // from the click handler, so a failure used to vanish silently (AC-765) — now caught and logged instead.
    private async Task _OpenChatAsync()
    {
        try
        {
            // AC-953: which host the chat opens in is the stand the operator last left it in, and it survives a
            // restart because it is read back off `LayoutSettings`.
            await _ShowInAsync(_cockpit.AssistantDocked).ConfigureAwait(true);

            // AC-1013: AC-959 — window first, session after. Starting the session is slow (MCP catalog, loopback
            // endpoints, OAuth renewal, CLI spawn, transcript replay — measured 3.4s to spawn on one open, more
            // with a slow sign-in); the window shows "not started yet" meanwhile, and the await still reaches the catch.
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

    // AC-1013: the rail is the only caller of this factory, so being built by it *is* being docked. Two routes
    // bypass `_ShowInAsync` (the rail tab click, the startup restore), so this runs the handover too — timed so
    // the window's view detaches (handing over scroll position) before the returned view reads it.
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

            // AC-1013: shown without an owner (deliberately, so it stays reachable via global hotkey while the
            // cockpit is backgrounded), closed with the cockpit by hand — without it, Avalonia's "shutdown on
            // last window closed" left an ownerless pop-out keeping the process (and F10) alive after cockpit close.
            if (Avalonia.Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            {
                // AC-962: the window measures its drop zone against the cockpit, and this is already the one
                // place that looks the main window up.
                _chatWindow.CockpitWindow = main;

                main.Closed += _OnMainWindowClosed;
                _chatWindow.Closed += (_, _) => main.Closed -= _OnMainWindowClosed;
            }
        }

        WindowActivation.BringToFront(_chatWindow);
    }

    // AC-953: the single place that decides where the chat stands, so no route leaves two on screen. Callers say
    // which host they want (idempotent). Old host torn down first, new one built after, so the leaving view
    // writes its scroll position before the arriving view reads it — build-then-tear-down would read it stale.
    private async Task _ShowInAsync(bool docked)
    {
        // AC-1256: the freeze that ticket is about started seconds after an undock, and the log held no record
        // that one had happened — the whole correlation rested on the operator remembering. One line, so the next
        // reconstruction does not have to.
        _logger.LogInformation("Assistant chat moving to {Host}.", docked ? "the dock rail" : "its own window");

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

    // Switches the microphone between held-only and held-open. Wake-word mode is refused rather than absent
    // from the UI: the enum still carries it (its own future ticket), so a caller could still pass it — this
    // one-line guard keeps the microphone from opening on a filter that doesn't exist yet.
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
