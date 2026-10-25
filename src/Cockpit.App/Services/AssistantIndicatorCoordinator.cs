using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Cockpit.App.Docking;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Plugins.Abstractions.CompanionTools;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using Cockpit.Core.Sessions;
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
    private readonly IAssistantSessionHost _assistant;
    private readonly OpenMicCoordinator _openMic;
    private readonly VoiceOverlayCoordinator _overlay;
    private readonly IAssistantSettingsStore _settings;
    private readonly IVoicePlaybackQueue _playbackQueue;
    private readonly IAssistantSpawnAuditLog _spawnAuditLog;
    private readonly CockpitViewModel _cockpit;
    private readonly ILogger<AssistantIndicatorCoordinator> _logger;

    private readonly IDockPanelRegistry? _dockPanels;

    // Where the closed threads are kept across a restart; null in a graph without one, which keeps them for the run.
    private readonly IAssistantThreadStateStore? _threadState;

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
        IAssistantSessionHost assistant,
        OpenMicCoordinator openMic,
        VoiceOverlayCoordinator overlay,
        IAssistantSettingsStore settings,
        IVoicePlaybackQueue playbackQueue,
        IAssistantSpawnAuditLog spawnAuditLog,
        CockpitViewModel cockpit,
        IDockPanelRegistry? dockPanels = null,
        ILogger<AssistantIndicatorCoordinator>? logger = null,
        IAssistantThreadStateStore? threadState = null)
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
        _threadState = threadState;
    }

    // The chip the sidebar binds to. One instance, fed from here.
    public AssistantIndicatorViewModel Indicator { get; } = new();

    public CompanionToolRegistration CreateCompanionTool() =>
        new("cockpit.assistant", "Assistant", _ => new AssistantIndicator { DataContext = Indicator, ShowWhenDisabled = true })
        {
            IconKind = MaterialIconKind.Creation,
        };

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

        // Assistant threads (§ 7.3): with questions open, the chip and the tab's count both open them. Each of these
        // catches and logs its own failure, as _OpenChatAsync does since AC-765, so a click never loses one unseen.
        Indicator.Clicked += async (_, _) => await (Indicator.ShowsQuestions ? _OpenQuestionsAsync() : _OpenChatAsync()).ConfigureAwait(true);
        _questionsBadge.Activated += async (_, _) => await _OpenQuestionsAsync().ConfigureAwait(true);
        _cockpit.AssistantFloatingCardOpened += async row => await _OpenRowAsync(row).ConfigureAwait(true);
        Indicator.ListeningModeSelected += (_, mode) => _ = _ApplyListeningModeAsync(mode);

        _RegisterDockPanel();

        // AC-1301: the Simple stand builds its conversation column from this, the same way the rail builds its
        // panel from `DockPanelRegistration.CreateView` — a factory rather than a binding, because what stands
        // there is one view model with one live view and this is what decides which host holds it.
        _cockpit.CreateSimpleViewChatView = _CreateSimpleViewChatView;

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

        // Assistant threads: the session files its rows by project, and only the cockpit knows the projects.
        if (_assistant.View() is { Threads: null } session)
        {
            session.Threads = new ThreadLookup(
                projectId => _cockpit.Projects.Projects.FirstOrDefault(project => project.Id == projectId)?.Name,
                paneId => _PaneOf(paneId)?.ProjectId,
                paneId => _PaneOf(paneId)?.Title)
            {
                Infer = text => ProjectInference.Infer(
                    text,
                    [.. _cockpit.Projects.Projects.Select(project => new ProjectHint(project.Id, project.Name, project.EffectiveTicketPrefixes))],
                    [.. _cockpit.AllSessions()
                        .Where(pane => pane.PaneId != AssistantIdentity.PaneId)
                        .Select(pane => new SessionHint(pane.PaneId, pane.Title ?? string.Empty, pane.ProjectId))]),
                SessionsOf = projectId => [.. _RunningSessions()
                    .Where(pane => pane.ProjectId == projectId)
                    .Select(pane => string.IsNullOrEmpty(pane.SessionStatusLabel) ? pane.Title : $"{pane.Title} · {pane.SessionStatusLabel}")],
                LiveProjects = () => [.. _RunningSessions().Select(pane => pane.ProjectId).OfType<string>().Distinct(StringComparer.Ordinal)],
            };
        }

        _WatchQuestions(_assistant.View());
    }

    // The tab's count and the chip's, read off the one list of open questions the session keeps.
    private readonly DockPanelBadge _questionsBadge = new();

    private SessionViewModel? _questionsSession;

    private void _WatchQuestions(SessionViewModel? session)
    {
        if (ReferenceEquals(session, _questionsSession))
        {
            return;
        }

        if (_questionsSession is not null)
        {
            _questionsSession.ThreadsChanged -= _OnQuestionsChanged;
            _questionsSession.Transcript.CollectionChanged -= _OnAssistantRowsAdded;
            _questionsSession.ClosedThreadsChanged -= _OnClosedThreadsChanged;
        }

        _questionsSession = session;
        if (session is not null)
        {
            session.ThreadsChanged += _OnQuestionsChanged;
            session.Transcript.CollectionChanged += _OnAssistantRowsAdded;
            session.ClosedThreadsChanged += _OnClosedThreadsChanged;
            // AC-1343: left running on purpose — nothing waits on it, and it catches and logs its own failure.
            _ = _LoadClosedThreadsAsync(session);
        }

        _OnQuestionsChanged();
    }

    private async Task _LoadClosedThreadsAsync(SessionViewModel session)
    {
        try
        {
            if (_threadState is not null)
            {
                session.LoadClosedThreads(await _threadState.LoadClosedAsync().ConfigureAwait(true));
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The closed assistant threads could not be read back; every thread starts open.");
        }
    }

    // The session's event, so async void: everything sits inside the try, and a failure is logged rather than lost.
    private async void _OnClosedThreadsChanged()
    {
        try
        {
            if (_threadState is not null && _questionsSession is { } session)
            {
                await _threadState.SaveClosedAsync([.. session.ClosedThreads]).ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The closed assistant threads could not be saved; they stay closed for this run only.");
        }
    }

    private void _OnQuestionsChanged()
    {
        var count = _questionsSession?.OpenQuestionCount ?? 0;
        if (count > _questionsBadge.Count && _questionsSession?.OpenQuestions is [.., var newest])
        {
            _ShowFloatingCard(newest, newest.IsGeneral ? "Question" : $"Question from {newest.ThreadTags[0].Name}");
        }

        Indicator.OpenQuestionCount = count;
        _questionsBadge.Count = count;
    }

    // A message from another session is news too; a status line is not (§ 7.2, open question 5).
    private void _OnAssistantRowsAdded(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add || e.NewItems is null
            || _questionsSession is { IsReplayingTranscript: true })
        {
            return;
        }

        foreach (TranscriptEntryViewModel row in e.NewItems)
        {
            if (row.IsInboxRow)
            {
                _ShowFloatingCard(row, $"Message from {row.SenderLabel}");
            }
        }
    }

    // ponytail: one card at a time, the newest wins; a queue if several in a row turn out to get lost.
    private DispatcherTimer? _floatingCardTimer;

    private void _ShowFloatingCard(TranscriptEntryViewModel row, string title)
    {
        // Only where nothing else would say it: docked, outside the Simple stand, with the assistant's panel shut.
        if (!_cockpit.AssistantDocked || _cockpit.SimpleView || _cockpit.EffectiveOpenDockPanelId == DockPanelId)
        {
            return;
        }

        var body = row.Text.ReplaceLineEndings(" ").Trim();
        _cockpit.AssistantFloatingCard = new AssistantFloatingCardContent(title, body, row);

        _floatingCardTimer?.Stop();
        _floatingCardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _floatingCardTimer.Tick += (_, _) =>
        {
            _floatingCardTimer?.Stop();
            _cockpit.AssistantFloatingCard = null;
        };
        _floatingCardTimer.Start();
    }

    private async Task _OpenRowAsync(TranscriptEntryViewModel row)
    {
        await _OpenChatAsync().ConfigureAwait(true);
        try
        {
            _chatViewModel?.ShowInThreadCommand.Execute(row);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Showing a row in its assistant thread failed.");
        }
    }

    private async Task _OpenQuestionsAsync()
    {
        await _OpenChatAsync().ConfigureAwait(true);
        try
        {
            _chatViewModel?.OpenQuestionsCommand.Execute(null);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Opening the assistant's questions failed.");
        }
    }

    // Every session but the assistant itself: what a project still has running.
    private IEnumerable<SessionPanelViewModel> _RunningSessions() =>
        _cockpit.AllSessions().Where(pane => pane.PaneId != AssistantIdentity.PaneId);

    private SessionPanelViewModel? _PaneOf(string paneId) =>
        _cockpit.AllSessions().FirstOrDefault(session => session.PaneId == paneId);

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

    // AC-1710: registered once, on the generic contract. Where it stands is the host's floating stand, which also
    // takes the tab out of the rail while the assistant is a window; the window itself stays this coordinator's own.
    private void _RegisterDockPanel()
    {
        _cockpit.DockHost.UseOwnWindow(DockPanelId, _ShowChatWindow, () => _chatWindow?.Close());

        _dockPanels?.Register(new DockPanelRegistration(
            DockPanelId,
            "Assistant",
            MaterialIconKind.Creation,
            _CreateDockedChatView)
        {
            Badge = _questionsBadge,
            CanFloat = true,
        });
    }

    // AC-1013: the rail is the only caller of this factory, so being built by it *is* being docked. Two routes
    // bypass `_ShowInAsync` (the rail tab click, the startup restore), so this runs the handover too — timed so
    // the window's view detaches (handing over scroll position) before the returned view reads it.
    private Control _CreateDockedChatView()
    {
        // AC-1256/AC-1259: that freeze started seconds after a host swap and the log held no record of one. Here
        // rather than at the header button, because the two routes above reach this without passing it — and
        // before the work, so a swap that hangs still leaves the line saying it began.
        _logger.LogInformation("Assistant chat moving to the dock rail.");

        var chat = _EnsureChatViewModel();
        chat.IsDocked = true;
        chat.IsSimpleViewHost = false;
        _chatWindow?.Close();

        return new AssistantChatView { DataContext = chat };
    }

    // AC-1301: the Simple stand's conversation column, a third host beside the floating window and the dock
    // rail. Like the dock it takes the standing conversation over rather than copying it; unlike the dock it is
    // the only host that stand has, which is what `IsSimpleViewHost` tells the header.
    private Control _CreateSimpleViewChatView()
    {
        _logger.LogInformation("Assistant chat moving to the simple view's conversation column.");

        var chat = _EnsureChatViewModel();
        chat.IsDocked = true;
        chat.IsSimpleViewHost = true;
        _chatWindow?.Close();

        return new AssistantChatView { DataContext = chat };
    }

    private void _ShowChatWindow()
    {
        if (_chatWindow is null)
        {
            // The undock half of the line above. Inside this branch on purpose: bringing a window that already
            // stands to the front reparents nothing, and a line for it would read as a swap that never happened.
            _logger.LogInformation("Assistant chat moving to its own window.");

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

    // AC-953: which host the chat stands in — since AC-1710 decided by `DockPanelHost.ShowInAsync`, the one place
    // for every dock panel. Only the chat's own flags are set here, first, so a window closed by that move reads as
    // a hand-over to the rail and not as the operator letting go of the conversation.
    private async Task _ShowInAsync(bool docked)
    {
        if (_chatViewModel is { } chat)
        {
            chat.IsDocked = docked;

            // Both hosts this method reaches are outside the Simple stand, so whatever asked for one has
            // already taken the conversation out of that column.
            chat.IsSimpleViewHost = false;
        }

        await _cockpit.DockHost.ShowInAsync(DockPanelId, floating: !docked).ConfigureAwait(true);
    }

    // The header's Dock/Undock button: the other host, whichever this is.
    private async Task _ToggleDockAsync()
    {
        // AC-1303: in the Simple stand the dock is a role and not a setting, so this button means "give the main
        // column back" rather than "pop out into a window". It lands in the same state as picking the assistant's
        // own row in the rail, which is the other way back — one state, reached two ways (criterion 4(a)).
        if (_cockpit.SimpleStandDocksTheAssistant)
        {
            _cockpit.SimpleSelectedSession = _cockpit.AssistantRootSession;
            return;
        }

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
