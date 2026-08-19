using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Services;

// Spins up the voice assistant's own session and owns it (AC-543, decision 3).
// *Why the host makes it.* The assistant gets to see across every workspace, which is a level of reach no
// ordinary session has. If it were started the way other sessions are — through the delegation path, or as a pane
// — then "which session is the assistant" would be a claim something makes, and a claim can be made by anything
// that learns to make it. Here the host builds the instance and keeps the only reference to it, so the answer is
// settled by construction: `Session` *is* the assistant, and there is no sentence an agent can
// say that puts it in this field.
//
// *Lazily.* Nothing starts at app start — not on the first render, not on a timer. The first hold of the
// assistant hotkey or the first click on the chip is what brings it up, so an operator who has the feature on but
// never uses it pays for no model in memory and no session on a bill. The first-time wait is visible in the
// indicator (`AssistantActivity.Thinking` while it comes up) rather than spent as silence.
//
// *And it comes back.* A delegated task reaps itself when it is done; this does the opposite. A session that
// falls over quietly is only discovered the next time you ask it something — the silence this product refuses
// everywhere else — so `EnsureStartedAsync` notices a dead instance and stands a new one up in its
// place, resuming the same conversation. `RestartAsync` is the operator asking for the same thing on
// a healthy one, which is what makes a setting that can only be chosen at a launch reachable at all.
//
// *The conversation outlives everything.* The pop-out window is a view onto this session, never its owner:
// closing it leaves the instance running. Across a restart the thread is picked up the way every other session
// does it (AC-409/AC-410) — the state store's last record for `AssistantPaneId` names the
// conversation, and the start resumes it. No separate retention rule of its own, deliberately: this surface is
// the audit trail, and one that emptied on every restart would protect nothing.
//
// It implements `IAssistantSessionHost` only so the chat window can be built against something a
// test and the screenshotter can stand in for — this class needs a whole `CockpitViewModel` behind it. The
// interface is not a seam for a second implementation: there is one assistant, and that there is exactly one is
// the point.
public sealed partial class AssistantSessionHost : ObservableObject, ISingletonService, IAssistantSessionHost
{
    // The pane id the assistant is always known by. Fixed rather than a fresh guid per launch: the state store
    // keys the last conversation on the pane, so an id that changed every start would leave yesterday's
    // conversation on disk under a name nothing looks up again.
    //
    // Now also the identity the broad read tools check against (AC-544), which is why the value itself lives in
    // Core: Infrastructure hosts those tools and cannot see this assembly, and two copies of a guardrail's
    // constant is a guardrail that can quietly stop matching.
    internal const string AssistantPaneId = AssistantIdentity.PaneId;

    private readonly CockpitViewModel _cockpit;
    private readonly IAssistantSettingsStore _settings;
    private readonly IAssistantProfileStore _profiles;
    private readonly ISessionStateStore _sessionState;
    private readonly IMcpServerCatalog _mcpServers;
    private readonly IAssistantMemory _memory;
    private readonly IAssistantTranscriptStore _transcript;
    private readonly ILogger<AssistantSessionHost> _logger;

    // Serializes starts: a hotkey hold and a chip click landing together must not each build an instance.
    private readonly SemaphoreSlim _startGate = new(1, 1);

    // AC-740: backs DefaultWorkingDirectory below. Lazily kicked off by that property's first read, not the
    // constructor — most windows never open the @-mention picker before a session starts.
    private string? _defaultWorkingDirectory;
    private Task? _defaultWorkingDirectoryLoad;

    public AssistantSessionHost(
        CockpitViewModel cockpit,
        IAssistantSettingsStore settings,
        IAssistantProfileStore profiles,
        ISessionStateStore sessionState,
        IMcpServerCatalog mcpServers,
        IAssistantMemory memory,
        IAssistantTranscriptStore transcript,
        ILogger<AssistantSessionHost> logger)
    {
        _cockpit = cockpit;
        _settings = settings;
        _profiles = profiles;
        _sessionState = sessionState;
        _mcpServers = mcpServers;
        _memory = memory;
        _transcript = transcript;
        _logger = logger;
    }

    // The living assistant instance, or null while it has not been woken yet. The one reference there is.
    [ObservableProperty]
    private SessionViewModel? _session;

    // What the indicator reports. Fed from here rather than read off the session, because "off" and "never started" are states no session exists to report.
    [ObservableProperty]
    private AssistantActivity _activity = AssistantActivity.Unavailable;

    // Why the assistant cannot be reached, in words for the operator — the feature is off, no profile is set, or
    // the start failed. Non-null exactly while `Activity` is `AssistantActivity.Unavailable`:
    // an unavailable chip that does not say why sends someone into Options looking for a setting that is not the
    // problem.
    [ObservableProperty]
    private string? _unavailableReason = "The assistant is switched off. Turn it on in Options → Voice.";

    // AC-740: the picker's fallback working directory before a session exists. Lazily loaded on first read
    // (see the field above); the very first '@' before that resolves reads null, so the picker's own
    // null-workingDirectory guard just keeps it shut for that one instant.
    public string? DefaultWorkingDirectory
    {
        get
        {
            _defaultWorkingDirectoryLoad ??= _LoadDefaultWorkingDirectoryAsync();
            return _defaultWorkingDirectory;
        }
    }

    private async Task _LoadDefaultWorkingDirectoryAsync()
    {
        try
        {
            var slot = await _profiles.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            _defaultWorkingDirectory = slot.Profile?.DefaultWorkingDirectory;
            OnPropertyChanged(nameof(DefaultWorkingDirectory));
        }
        catch (Exception)
        {
            // Best-effort warm cache — a failed load here just leaves the picker's fallback unavailable until a
            // real session provides a working directory of its own.
        }
    }


    // The assistant hotkey went down or came back up. Reported here rather than left for the indicator to infer
    // from the shared voice pill — see `IAssistantSessionHost.ReportHoldListening` for what that
    // inference got wrong.
    // Only moves between Ready and Listening: a hold that ends hands over to `SendAsync`, which sets
    // Thinking, and neither may overwrite an Unavailable the operator still needs to read.
    public void ReportHoldListening(bool listening)
    {
        if (listening)
        {
            if (Activity == AssistantActivity.Ready)
            {
                Activity = AssistantActivity.Listening;
            }

            return;
        }

        if (Activity == AssistantActivity.Listening)
        {
            Activity = AssistantActivity.Ready;
        }
    }

    // Speech-to-text is working on what was just said (AC-543, 2026-08-08 — this used to be a line on the shared
    // voice pill). Guarded the same way as the hold above: it may not overwrite an Unavailable the operator still
    // needs to read, and the end of it hands back to Ready rather than to whatever came before, because what comes
    // next — SendAsync setting Thinking — is a beat later and the chip must not sit on a finished transcription.
    public void ReportTranscribing(bool transcribing)
    {
        if (transcribing)
        {
            if (Activity is AssistantActivity.Ready or AssistantActivity.Listening)
            {
                Activity = AssistantActivity.Transcribing;
            }

            return;
        }

        PreparationStatus = null;
        PreparationProgress = null;

        if (Activity is AssistantActivity.Transcribing or AssistantActivity.Preparing)
        {
            Activity = AssistantActivity.Ready;
        }
    }

    // The one-time model/runtime fetch in front of the first transcription. A step with no status ends it and
    // hands back to Transcribing — preparation always precedes an actual transcription, never a resting chip.
    public void ReportPreparing(string? status, double? fraction)
    {
        PreparationStatus = status;
        PreparationProgress = status is null ? null : fraction;

        if (status is null)
        {
            if (Activity == AssistantActivity.Preparing)
            {
                Activity = AssistantActivity.Transcribing;
            }

            return;
        }

        if (Activity is AssistantActivity.Ready or AssistantActivity.Listening or AssistantActivity.Transcribing
            or AssistantActivity.Preparing)
        {
            Activity = AssistantActivity.Preparing;
        }
    }

    // What speech-to-text is fetching right now, and how far along it is where that is known — shown on the chip
    // beside `AssistantActivity.Preparing`. Null whenever nothing is being prepared.
    [ObservableProperty]
    private string? _preparationStatus;

    [ObservableProperty]
    private double? _preparationProgress;

    // Brings the assistant up if it is not already, and returns it. Idempotent, and the recovery path too: an
    // instance that died is replaced rather than handed back dead.
    // Never throws. Its callers are a hotkey handler and a click handler, neither of which has anywhere to put an
    // exception — and what a swallowed one would take with it is the assistant, silently. A failed start leaves
    // `Activity` on `AssistantActivity.Unavailable` with the reason set, which is the
    // chip saying out loud what the log used to say alone.
    public Task<SessionViewModel?> EnsureStartedAsync(CancellationToken cancellationToken = default) =>
        _StartOrReplaceAsync(replaceALiveInstance: false, startFresh: false, cancellationToken);

    // Stands the assistant down and brings it straight back up on the same conversation — the operator's way to
    // make a start-time setting take effect without closing the cockpit.
    // *Why a restart has to exist at all.* A permission mode is chosen at a start and, for
    // `bypassPermissions`, only at a start: the CLI cannot be switched into or out of bypass live, which is
    // why `SessionOptionCatalog` keeps `LivePermissionModes` apart from `AllPermissionModes`
    // (bug #15, and the no-dead-controls convention). Every other session gets its next start for free — it is
    // closed and opened again. The assistant cannot be closed: it is not in the grid, its window is a peephole
    // that deliberately never ends it, and `EnsureStartedAsync` only replaces an instance that is
    // already *dead*. So "this applies at the next start" was a sentence with no next start behind it.
    //
    // *The conversation is kept.* This is a restart, not a reset: it runs the same
    // `_StartAsync` as every other start, which resumes through `_ResolveResumeAsync` —
    // the state store's last record for `AssistantPaneId`. Losing the thread here would defeat the
    // whole reason that record exists, and this surface is the audit trail.
    //
    // *And it is the same teardown.* Not a second one: `_DisposeQuietlyAsync` is what the
    // replace-a-dead-instance path already runs, so a turn in flight, the host's own subscription, the cockpit's
    // consent routing and an unanswered consent card are all handled once, in one place, however the instance
    // came to be replaced.
    public Task<SessionViewModel?> RestartAsync(CancellationToken cancellationToken = default) =>
        _StartOrReplaceAsync(replaceALiveInstance: true, startFresh: false, cancellationToken);

    // How full the context may get before the assistant hands itself over and starts again (AC-596). A percentage
    // rather than a token count, because it is the provider that reports the fill and it knows the window.
    // ponytail: one number for every provider. High enough that an ordinary exchange never reaches it, low enough
    // to leave room for the turn that crosses it plus the one that answers the operator afterwards.
    internal const double RestartAboveContextPercent = 80;

    // `replaceALiveInstance`:
    // Whether a healthy instance is torn down too. False is `EnsureStartedAsync`'s idempotent lazy
    // start; true is `RestartAsync`. One body rather than two so both take the same start gate — a
    // restart racing a hotkey hold must not build two instances any more than two holds may.
    // `startFresh`: whether the new instance picks up the conversation (every other start) or deliberately does
    // not (AC-596's hand-over, where dropping the transcript is the entire point).
    private async Task<SessionViewModel?> _StartOrReplaceAsync(
        bool replaceALiveInstance,
        bool startFresh,
        CancellationToken cancellationToken,
        string? startFreshBecause = null)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (!replaceALiveInstance && Session is { } live && _IsAlive(live))
            {
                return live;
            }

            // A dead instance is dropped before a new one is built, so a start that fails does not leave the
            // corpse in place looking reachable.
            if (Session is { } previous)
            {
                _logger.LogInformation(
                    replaceALiveInstance
                        ? "Restarting the assistant session on the same conversation."
                        : "The assistant session had stopped; starting a new one on the same conversation.");
                Session = null;
                await _DisposeQuietlyAsync(previous).ConfigureAwait(true);
            }

            return await _StartAsync(startFresh, cancellationToken, startFreshBecause).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The assistant could not be started.");
            _SetUnavailable("The assistant could not be started — see the log.");
            return null;
        }
        finally
        {
            _startGate.Release();
        }
    }

    // Sends one utterance or typed line to the assistant, starting it first if this is the first time. The single
    // entry point for both input paths, so speaking and typing reach the same conversation by the same route —
    // which is what makes the assistant fully usable with no microphone at all.
    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        // An image with no words is a message too (AC-630) — a pasted or captured attachment waiting on the
        // composer is reason enough to send, and refusing here left it hanging with no way out.
        if (string.IsNullOrWhiteSpace(text) && Session is not { HasPendingAttachments: true })
        {
            return;
        }

        // Reported before the start, not after: bringing the instance up the first time takes long enough that the
        // operator is owed something on screen for it, and "thinking" is what that wait is.
        Activity = AssistantActivity.Thinking;

        if (await EnsureStartedAsync(cancellationToken).ConfigureAwait(true) is not { } session)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            // Attachment only: InjectAndSubmit returns on empty text, so the composer's own send path takes it —
            // which is the one that picks the pending attachments up.
            session.SendCommand.Execute(null);
            return;
        }

        session.InjectAndSubmit(text.Trim());
    }

    // Re-reads the settings and stands the assistant down if the feature was switched off — including mid-sentence,
    // which is the point: whoever clicks off wants silence, not one more paragraph.
    public async Task ApplySettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(true);

        // Everything the start path applies from these settings is re-applied here, so a save reaches the running
        // assistant instead of only its next start. Written as "the start path's own call, again" rather than a
        // hand-picked field or two: picking is what left SpeakReplies behind while the header checkbox moved, and
        // the next field added to _ApplySpeech would have been left behind the same way.
        if (Session is { } live)
        {
            live.ReadingLevel = settings.ReadingLevel;
            _ApplySpeech(live, settings);
        }

        if (settings.IsEnabled)
        {
            // Deliberately does not start anything: switching the feature on makes the assistant available, and
            // the first hold or click is still what wakes it.
            if (Session is null)
            {
                Activity = AssistantActivity.Ready;
                UnavailableReason = null;
            }

            return;
        }

        var stopping = Session;
        Session = null;
        _SetUnavailable("The assistant is switched off. Turn it on in Options → Voice.");

        if (stopping is not null)
        {
            await _DisposeQuietlyAsync(stopping).ConfigureAwait(true);
        }
    }

    private async Task<SessionViewModel?> _StartAsync(
        bool startFresh, CancellationToken cancellationToken, string? startFreshBecause = null)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(true);
        if (!settings.IsEnabled)
        {
            // Criterion 1: with the feature off the hotkey does nothing — and says why, rather than being a key
            // that quietly is not there.
            _SetUnavailable("The assistant is switched off. Turn it on in Options → Voice.");
            return null;
        }

        var slot = await _profiles.LoadAsync(cancellationToken).ConfigureAwait(true);
        if (slot.Profile is not { } profile)
        {
            _SetUnavailable(slot.UnsetReason ?? "No Assistant Profile is set. Pick one in Options → Voice.");
            return null;
        }

        var session = _cockpit.CreateAssistantSession(AssistantPaneId);
        if (session is null)
        {
            _SetUnavailable("This cockpit cannot start sessions.");
            return null;
        }

        Activity = AssistantActivity.Thinking;
        UnavailableReason = null;

        // Picks up yesterday's conversation when there is one — the same resume the restore path uses, rather
        // than a retention rule invented here.
        var resume = startFresh
            ? SessionResume.New
            : await _ResolveResumeAsync(cancellationToken).ConfigureAwait(true);

        // AC-684: replay before the launch so the window shows the earlier conversation the moment it attaches,
        // not after — a resume the provider ends up refusing (below) throws this whole session away anyway.
        if (resume.Mode == SessionResumeMode.BySessionId)
        {
            await _ReplayTranscriptAsync(session, cancellationToken).ConfigureAwait(true);
        }
        else
        {
            // AC-947: nothing will replay this file's rows into the new session, and the first row this launch
            // saves overwrites it — archive it now, while it still holds the conversation before this restart.
            await _transcript.ArchiveAsync(cancellationToken).ConfigureAwait(true);
        }

        await session.StartConfiguredAsync(
            profile,
            // The app defaults, and only as the floor. The Assistant Profile's own permission mode, model and
            // effort ride the launch options below — the single route a plugin profile's start defaults take on
            // every other launch in this app — and the driver takes those over these. A profile that says nothing
            // therefore still starts exactly where it did before. See _LaunchOptions for the whole rule, including
            // what an operator is choosing when they put bypassPermissions on this profile.
            SessionOptionCatalog.DefaultPermissionMode,
            SessionOptionCatalog.DefaultModel,
            SessionOptionCatalog.DefaultEffort,
            resume: resume,
            // The one place in the codebase that names the broad read server (AC-544). See _McpSelectionAsync.
            enabledMcpServerNames: await _McpSelectionAsync(profile, cancellationToken).ConfigureAwait(true),
            launchOptions: _LaunchOptions(
                profile,
                slot.ReplacesStandingInstruction,
                await _memory.ReadAsync(cancellationToken).ConfigureAwait(true),
                await _memory.ReadCurrentStateAsync(cancellationToken).ConfigureAwait(true),
                _SdkAsksPermission(profile),
                // Gate B (AC-759): read off ConsentBypassAll alone, not the two per-source lists too — the
                // paragraph is describing what the operator should generally expect, and the per-call `approval`
                // field (AssistantAgentMcpTools) is what corrects the record exactly when only one source was
                // switched off individually. Reading the lists here would let the paragraph promise a click that a
                // specific source's own bypass then never raises — the same defect this ticket reported, one gate
                // over.
                consentCardAsks: !settings.ConsentBypassAll),
            readingLevel: settings.ReadingLevel).ConfigureAwait(true);

        // A start that did not take leaves its only trace in Status: SessionViewModel.StartConfiguredAsync
        // catches every launch exception and writes it there without logging it, so the host's restart loop is
        // otherwise the whole of the evidence and the cause is gone by the time anyone reads the log.
        if (!_IsAlive(session))
        {
            _logger.LogWarning("The assistant session was not running right after its start: {Status}", session.Status);
        }

        // AC-638/AC-596: say why in the transcript, since the hand-over note only reaches the system prompt.
        // `startFreshBecause` lets AC-684's failed-resume recovery use its own reason instead of this default.
        if (startFresh)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(
                TranscriptEntryKind.Divider,
                startFreshBecause ?? "Context was full — a new conversation starts here, picked up from a short note"));
        }
        else if (resume.Mode == SessionResumeMode.BySessionId)
        {
            // AC-684: watched rather than awaited — a refused resume surfaces as a normal-looking return from
            // StartConfiguredAsync, and blocking every successful resume on a grace window would tax the common case.
            _WatchForUnresolvableResume(session);
        }

        _ApplySpeech(session, settings);

        // A new instance has a new context, and a provider that reports no fill until its first turn would otherwise
        // never take the below-the-line reset — leaving the ask spent before this conversation had used anything.
        _askedTheProviderToCompact = false;
        Session = session;

        // The wire that makes Thinking end. Everything else here sets Activity at a moment the host knows about —
        // a hold, a send, a start, a failure — and none of those is the moment a turn finishes, because only the
        // session knows that. Without this the chip is written to on the way in and never on the way out: the
        // first send lands on Ready (set two lines up, after the send) while the assistant is plainly thinking,
        // and every send after that leaves it on Thinking for good, because EnsureStartedAsync returns a live
        // instance without touching Activity. Both are the same missing subscription rather than two bugs.
        session.PropertyChanged += _OnSessionPropertyChanged;

        // AC-684: every new row this session ever adds — the replayed history is already in by now, so this only
        // ever sees rows the operator has not seen persisted yet.
        session.Transcript.CollectionChanged += _OnTranscriptChanged;
        _SyncActivityWithSession(session);
        return session;
    }

    // Gives the assistant's session what it needs to actually be heard — decision 2's "TTS erna", which nothing
    // was doing.
    // *Why it has to be done here at all.* Nothing else seeds a starting session's speech: the cockpit's
    // voice fan-out fires on an Options save, and a session started after the last save would keep the bare
    // defaults — `ReadResponsesAloud` false, which makes the read-aloud flush return before it speaks a word,
    // and `ReadAloudLanguage` "en", which would read Dutch replies in an English voice. The operator heard
    // nothing at all and there was nothing on screen to say why.
    //
    // Called from `ApplySettingsAsync` as well as from the start, so an Options save reaches the
    // running instance. The voice and language additionally arrive through the cockpit's own fan-out, which now
    // includes the assistant; both routes write the same two values, and neither is load-bearing alone — the
    // fan-out does not fire at start, and this does not fire when only the voice settings are saved.
    //
    // *Read-aloud speaks the reply verbatim, never a rewrite.* AC-542 decision 10 is explicit that the
    // assistant's words go out one-to-one: what shortens a 300-word answer is
    // `AssistantSystemPrompt.Default`, not a rewrite afterwards. Read-aloud has no rewrite step left
    // to pick a mode for (AC-546) — it only ever extracts and speaks the prose as-is.
    //
    // The voice and language do follow the operator's settings, read off the cockpit's already-resolved
    // selections — the same values the fan-out to ordinary sessions uses, rather than a second copy of them.
    private void _ApplySpeech(SessionViewModel session, AssistantSettings settings)
    {
        // One synthesis for the whole reply instead of one per sentence. Measured on this machine, sentence-by-
        // sentence spent about as long synthesising as speaking, so every full stop came with an audible hole in
        // it — for a surface whose entire output is speech, that is not a rough edge but the product.
        session.ReadAloudAsOneUtterance = true;

        session.TtsVoiceSid = _cockpit.SelectedTtsVoice.Sid;
        session.ReadAloudLanguage = _cockpit.SelectedReadAloudLanguage.Code;
        session.ReadResponsesAloud = settings.SpeakReplies;
    }

    // Turns speaking on or off on the live session, so the header toggle takes effect on the next reply rather
    // than at the next restart. Does not stop what is already playing — that is the toggle's own job, and it
    // already does it (AC-543 criterion 9: off breaks off mid-sentence).
    public void SetSpeakReplies(bool speak)
    {
        if (Session is { } session)
        {
            session.ReadResponsesAloud = speak;
        }
    }

    private void _OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null
                or nameof(SessionViewModel.IsBusy)
                or nameof(SessionViewModel.HasPendingPermission)
                // The cockpit's own consent gate (#AC-47) is a second way to be waiting on the operator, and it
                // moves a different property than the SDK's permission does. Left out, the chip read "Ready" while
                // a k8s or terminal request sat unanswered over the chat window.
                or nameof(SessionPanelViewModel.PendingConsent)
                // AC-596: the same three properties decide whether it may hand over, so the fill is watched here
                // rather than on its own subscription — a context that crossed the line while it was still talking
                // has to be reconsidered the moment it stops.
                or nameof(SessionPanelViewModel.ContextUsedPercent)
            && sender is SessionViewModel session)
        {
            _SyncActivityWithSession(session);

            // Whether this change carries a *new* fill figure or merely happens to be able to read the last one: the
            // session refreshes the provider's limits after it has published IsBusy false, so the busy transition
            // arrives with the previous turn's figure still standing. Why that matters: _HandOverIfTheContextIsFull.
            _HandOverIfTheContextIsFull(
                session,
                fillWasJustRead: e.PropertyName is null or nameof(SessionPanelViewModel.ContextUsedPercent));
        }
    }

    // Relieves a context that is nearly full (AC-596) — but only while nothing is running and nothing is waiting on
    // the operator: that permission row belongs to a session that would no longer exist to receive the answer.
    // AC-664: a provider that can summarise its own conversation is asked to, and the restart is what is left.
    private void _HandOverIfTheContextIsFull(SessionViewModel session, bool fillWasJustRead)
    {
        if (!ReferenceEquals(session, Session))
        {
            return;
        }

        // Once a compaction has been asked for, only a fresh reading may decide anything: its turn ends with the
        // pre-compaction fill still standing, and judged there the hand-over would throw away the very conversation
        // the compaction had just saved.
        if (_askedTheProviderToCompact && !fillWasJustRead)
        {
            return;
        }

        // A fill that came back under the line re-arms the ask: this is the only place that can tell a compaction
        // that worked from one that did not, and the next crossing is a new episode rather than a repeat of this one.
        if (session.ContextUsedPercent < RestartAboveContextPercent)
        {
            _askedTheProviderToCompact = false;
            return;
        }

        if (!ShouldHandOver(
                session.ContextUsedPercent,
                session.IsBusy,
                session.HasPendingPermission || session.PendingConsent is not null))
        {
            return;
        }

        // Not awaited: this runs off a property change with nowhere to put a failure, and both branches report their
        // own — _StartOrReplaceAsync leaves the chip unavailable with the reason on it, and a compaction that could
        // not be asked for falls through to that restart rather than being lost.
        _ = _RelieveTheFullContextAsync(session);
    }

    // Whether the provider has already been asked to compact this fill. Without it, every property change above the
    // line would send another `/compact` at a provider that answered the first one with "nothing to compact" — and
    // the ask is what makes the fill move, so the condition that triggered it is still true when the reply lands.
    private bool _askedTheProviderToCompact;

    private async Task _RelieveTheFullContextAsync(SessionViewModel session)
    {
        if (session.Capabilities.SupportsContextCompaction && !_askedTheProviderToCompact)
        {
            _askedTheProviderToCompact = true;
            _logger.LogInformation(
                "The assistant's context is {Fill:0}% full; asking the provider to compact it.",
                session.ContextUsedPercent);

            if (await session.CompactContextAsync().ConfigureAwait(true))
            {
                // AC-638's divider, for the case that keeps the conversation. A compaction is otherwise invisible
                // here — the provider reports it as a system line the transcript does not render — so the assistant's
                // memory of the early part would quietly thin out with nothing to say that it had.
                session.Transcript.Add(new TranscriptEntryViewModel(
                    TranscriptEntryKind.Divider,
                    "Context was full — the conversation so far was summarised and continues here"));
                return;
            }
        }

        if (session.Capabilities.SupportsContextCompaction)
        {
            _logger.LogInformation(
                "The assistant's context is {Fill:0}% full and compacting did not relieve it; restarting it on a fresh conversation.",
                session.ContextUsedPercent);
        }
        else
        {
            _logger.LogInformation(
                "The assistant's context is {Fill:0}% full and this provider cannot compact; restarting it on a fresh conversation.",
                session.ContextUsedPercent);
        }

        await _StartOrReplaceAsync(replaceALiveInstance: true, startFresh: true, CancellationToken.None).ConfigureAwait(true);
    }

    // The rule itself, as a pure function so it can be asserted directly — the same shape as ActivityFor above.
    // A null fill is a provider that reported nothing this turn, which says nothing about how full the context is:
    // reading it as zero would postpone the hand-over indefinitely on a provider that only reports sometimes.
    internal static bool ShouldHandOver(double? contextUsedPercent, bool isBusy, bool isWaitingOnOperator) =>
        contextUsedPercent >= RestartAboveContextPercent && !isBusy && !isWaitingOnOperator;

    // Maps the session's own status onto what the chip reports.
    // Deliberately narrow. It only ever moves between `AssistantActivity.Thinking` and
    // `AssistantActivity.Ready`, and it refuses to speak over the two states the host owns and the
    // session knows nothing about: `AssistantActivity.Unavailable` is a fact about the feature rather
    // than about a turn, and `AssistantActivity.Listening` is a key being held right now — a turn
    // completing mid-hold must not tell the operator the microphone closed.
    //
    // Written as the set that means "working" rather than the set that means "done", so a status added later
    // arrives as Ready and has to be argued into Thinking deliberately — the same direction
    // `WorkspaceAgentGateway`'s wake check is written in, and for the same reason.
    private void _SyncActivityWithSession(SessionViewModel session) =>
        // Either kind of waiting counts: the SDK's own permission row, and the cockpit's consent gate for a
        // host-side tool. Both stop the turn dead until somebody clicks, and the chip's job is to say so.
        Activity = ActivityFor(Activity, session.IsBusy, session.HasPendingPermission || session.PendingConsent is not null);

    // The rule itself, as a pure function so it can be asserted directly. Internal for that and no other caller.
    // *Both inputs are read raw, and neither is `SessionPanelViewModel.SessionStatus`.* That
    // status is derived for a different audience and carries a deliberate stickiness this surface cannot use:
    // `_needsAttention` is set when a prompt appears and cleared only when the operator sends their next
    // message, and it outranks busy in the derivation. Reading it cost two wrong chips in a row — first stuck on
    // "Needs you" long after the approval was given and the reply spoken, then, once the pending flag was read
    // properly, stuck on "Ready" while the assistant was plainly working, because a session that still carries
    // NeedsAttention never reports Busy at all. Right for a sidebar you are not looking at; useless for a chip
    // that answers "what is it doing right now".
    //
    // So: is a decision waiting, and is it working. Two facts, each read from where it actually lives.
    internal static AssistantActivity ActivityFor(
        AssistantActivity current, bool isBusy, bool hasPendingPermission) => current switch
    {
        AssistantActivity.Unavailable or AssistantActivity.Listening => current,
        // Ahead of busy: a session can still be working on something while it stands on a prompt, and what the
        // operator needs to know is the half they can act on.
        _ when hasPendingPermission => AssistantActivity.AwaitingOperator,
        _ => isBusy ? AssistantActivity.Thinking : AssistantActivity.Ready,
    };

    // The conversation to pick up: the one the state store last recorded for this pane, or a fresh one when there
    // is none (a first run, or a store that could not be read).
    // Internal so the rule can be asserted directly — a restart's whole promise is that it picks this
    // conversation up rather than starting a fresh one, and that promise lives here.
    internal async Task<SessionResume> _ResolveResumeAsync(CancellationToken cancellationToken)
    {
        var states = await _sessionState.LoadAsync(cancellationToken).ConfigureAwait(true);
        return states.FirstOrDefault(state => string.Equals(state.PaneId, AssistantPaneId, StringComparison.Ordinal))
            is { ConversationId: { Length: > 0 } conversationId }
            ? SessionResume.BySessionId(conversationId)
            : SessionResume.New;
    }

    // AC-684: repaints a fresh session's transcript with what the operator saw before this launch. A row this
    // build cannot make sense of is skipped, same contract `SessionStateStore` uses for a line it cannot parse.
    private async Task _ReplayTranscriptAsync(SessionViewModel session, CancellationToken cancellationToken)
    {
        foreach (var saved in await _transcript.LoadAsync(cancellationToken).ConfigureAwait(true))
        {
            if (_FromSnapshotEntry(saved) is { } entry)
            {
                session.Transcript.Add(entry);
            }
        }
    }

    // AC-684: every new transcript row is worth a snapshot — fire-and-forget, same contract as
    // `ISessionStateStore.RecordAsync` (a failed save is logged, not the turn that produced the row).
    // ponytail: whole transcript re-serialized per row; upgrade to a debounced/incremental write if a long-running conversation makes the rewrite measurable.
    private void _OnTranscriptChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (sender is not ObservableCollection<TranscriptEntryViewModel> transcript)
        {
            return;
        }

        _ = _transcript.SaveAsync([.. transcript.Select(_ToSnapshotEntry)], CancellationToken.None);
    }

    private static AssistantTranscriptSnapshotEntry _ToSnapshotEntry(TranscriptEntryViewModel entry) => new(
        entry.Kind.ToString(),
        entry.Text,
        entry.ToolName,
        entry.InputJson,
        entry.ToolUseId,
        entry.ResultText,
        entry.IsResultError,
        entry.Timestamp);

    private static TranscriptEntryViewModel? _FromSnapshotEntry(AssistantTranscriptSnapshotEntry record)
    {
        if (!Enum.TryParse<TranscriptEntryKind>(record.Kind, out var kind))
        {
            return null;
        }

        var entry = new TranscriptEntryViewModel(kind, record.Text, record.Timestamp)
        {
            ToolName = record.ToolName,
            InputJson = record.InputJson,
            ToolUseId = record.ToolUseId,
        };

        if (record.ResultText is not null)
        {
            entry.SetResult(record.ResultText, record.IsResultError);
        }

        return entry;
    }

    // AC-684, criterion 4: a `BySessionId` resume the provider refuses surfaces as an immediate failed turn
    // (AC-539's `error_during_execution`), not an exception. The first row this fresh launch's transcript
    // receives decides it, once, since nothing has sent the provider a prompt yet to correlate against.
    private void _WatchForUnresolvableResume(SessionViewModel session)
    {
        void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            session.Transcript.CollectionChanged -= OnChanged;

            if (e.NewItems?.Cast<TranscriptEntryViewModel>().FirstOrDefault()
                    is { Kind: TranscriptEntryKind.TurnCompleted } entry
                && ReferenceEquals(session, Session))
            {
                _ = _RecoverFromUnresolvableResumeAsync(session, entry.Text);
            }
        }

        session.Transcript.CollectionChanged += OnChanged;
    }

    // Drops the session whose resume the provider refused and starts over clean, the same replace-a-dead-instance
    // shape `_RelieveTheFullContextAsync` uses for AC-596's hand-over — but with its own reason on the divider
    // rather than that one's "context was full", so the operator reads what actually happened.
    private async Task _RecoverFromUnresolvableResumeAsync(SessionViewModel session, string reason)
    {
        _logger.LogInformation(
            "The assistant's earlier conversation could not be resumed ({Reason}); starting a new one.", reason);

        await _StartOrReplaceAsync(
            replaceALiveInstance: true,
            startFresh: true,
            CancellationToken.None,
            startFreshBecause: $"Could not resume the previous conversation ({reason}) — a new one starts here"
        ).ConfigureAwait(true);
    }

    // The MCP servers the assistant launches with: what it would have had anyway, plus the broad read server that
    // only it may mount (AC-544, criterion 2).
    // *This is the mount rule.* `cockpit-assistant` is registered as an internal endpoint, which means it
    // never reaches a session through the no-selection fan-out and never appears in a picker for anyone to tick —
    // it is mounted only by a launch that names it, and this line is the only one that does. That is exclusion by
    // construction rather than by permission check: the reason an ordinary session does not get these tools is that
    // nothing hands them to it, not that something decided not to.
    //
    // *Why the rest of the selection has to be spelled out.* Passing an explicit set overrides the profile's own
    // saved one (`McpServerRegistryFilter.EffectiveSessionSelection`), and passing *only* the assistant
    // server would therefore leave the assistant with nothing else — no Depot, no YouTrack, none of what the epic
    // expects it to reach. So the profile's selection is carried through when it has one, and when it has none the
    // set is what the no-selection fan-out would have given it: every enabled server that is a choice at all.
    // `OfferedToOperator` is asked for that rather than a fourth hand-written copy of the same predicate — and
    // asking it is also what keeps *other* internal endpoints out of this set. Widening one privileged
    // launch into "and every internal endpoint too" is precisely the accident this rule exists to make impossible.
    //
    // A catalog that cannot be read is not a reason to start with a crippled assistant, but it is also not a reason
    // to invent a selection: the failure is logged and the assistant launches with the broad server alone, which is
    // the one thing this method is actually responsible for. Reporting less would be a silent downgrade.
    private async Task<IReadOnlySet<string>> _McpSelectionAsync(
        Cockpit.Core.Profiles.SessionProfile profile, CancellationToken cancellationToken)
    {
        // The catalog is only needed for the no-saved-selection case, and a catalog that cannot be read is not a
        // reason to fail the launch — but it is a reason to say so, because the assistant then comes up with fewer
        // tools than the operator configured and nothing else would report that.
        IReadOnlyList<McpServerConfig> catalog = [];
        if (profile.EnabledMcpServerNames is null)
        {
            try
            {
                catalog = await _mcpServers.GetServersAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "The MCP catalog could not be read for the assistant's launch; it starts with its own read tools only.");
            }
        }

        return McpSelection(profile, catalog);
    }

    // The selection itself, as a pure function of the profile and the catalog — so the rule that matters can be
    // asserted directly rather than inferred from a started session. Internal for that test and for no other
    // caller.
    internal static IReadOnlySet<string> McpSelection(
        Cockpit.Core.Profiles.SessionProfile profile, IReadOnlyList<McpServerConfig> catalog)
    {
        // Both of the assistant's own endpoints, named here and nowhere else — the read half (AC-544) and the acting
        // half (AC-545). Both are Internal, so this launch is the only way either is reached at all; and both check
        // the caller's pane in every tool, so this being the only mount is the first of two gates rather than the
        // only one. Adding the acting server here is what makes start_agent exist for the assistant — and a launch
        // that named only the read server would leave AC-545's tools registered, mounted nowhere, and silently absent.
        var selection = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AssistantIdentity.McpServerName,
            AssistantIdentity.ActMcpServerName,
            // AC-869: the assistant always has cockpit-github-pull-requests, regardless of working directory —
            // the one launch other than a git-repo session that names this internal endpoint.
            GitHubPullRequestsMcp.ServerName,
        };

        // A saved selection is the operator's own answer and is taken whole — including anything below, if they
        // ticked it deliberately. The filter is about what an assistant gets when nobody said.
        selection.UnionWith(profile.EnabledMcpServerNames
            ?? [.. McpServerRegistryFilter.OfferedToOperator(catalog)
                .Select(server => server.Name)
                .Where(name => !NotFannedOutToTheAssistant.Contains(name))]);
        return selection;
    }

    // Servers the assistant is deliberately not handed by the no-selection fan-out (AC-545, Raymond 2026-08-01).
    // *One name, and the bar for a second one is high.* The assistant is meant to be over the whole cockpit
    // (AC-545: "hij is overkoepelend over alles"), so a server is kept out only when there is something structural
    // wrong with it being here — never because a category of tool feels like a lot of authority to hand to a voice.
    // The first draft of this list also held the shell, containers, the cluster, worktrees, the repo's checks and
    // its workflows, on that reasoning. It does not hold: every one of those raises its own Allow/Deny row in the
    // chat window with the literal action spelled out, which is the same gate this ticket built for spawning. A
    // tool that goes through the gate is not a reason to remove the tool.
    //
    // `cockpit-orchestrator` is different in kind, not in degree. `delegate_task` starts real AI work with
    // *no pane*: it appears in no roster, raises no Allow row of the kind this ticket built, and is written
    // to no spawn trail — a second way to start work that goes around every guarantee AC-545 put in front of the
    // first, rather than through them. Asked for "the same profile but as an SDK session" before
    // `start_agent` had a route parameter, the assistant went looking for it there: a missing parameter and an
    // open side door meet, and the guardrail is what loses.
    //
    // Still a default and not a boundary: an operator who wants it here ticks it on the Assistant Profile and gets
    // it, selection and all. `AlwaysMounted` endpoints (`cockpit-session`, `cockpit-agents`) are not
    // reachable from here by construction — they go to every session whatever any selection says.
    internal static readonly HashSet<string> NotFannedOutToTheAssistant = new(StringComparer.OrdinalIgnoreCase)
    {
        Cockpit.Core.Delegation.DelegationMcp.ServerName,
    };

    // What the assistant's session launches with: the Assistant Profile's own start defaults, plus the assistant's
    // standing instruction on the launch option every provider honours.
    // *The profile's defaults are the whole of how a profile is obeyed.* A plugin profile does not carry its
    // permission mode, model and effort in `Defaults.PermissionMode/Model/Effort` — those three are legacy,
    // kept only for the one-time migration (see `Cockpit.Core.Profiles.ProfileDefaults`'s own
    // `[Obsolete]` notes) — it carries them in the generic `OptionDefaults` map, which travels as launch
    // options and is read by the driver (`ClaudeSdkSessionDriver._ResolveOption`). Starting that map here is
    // exactly what every other launch does: the New-session dialog seeds its option rows from
    // `OptionDefaults` and hands them back as the launch options, and every programmatic start passes
    // `profile.Defaults?.OptionDefaults` straight through (`CockpitViewModel._EmbeddedLaunchOptions` and
    // the plugin/quick-start paths). Without it the assistant was the one session in the app that read a profile
    // and then ignored what it said: an operator's `bypassPermissions` reached the driver as "default" and
    // every tool call was still asked about.
    //
    // *The typed mode/model/effort at the call site stay on the app defaults, deliberately.* They are the
    // fallback, not the answer: `PluginSessionDriverAdapter._MergePermissionMode` only folds the typed value
    // in when the options carry none, so a profile that says nothing lands on the app default and a profile that
    // says something wins. Seeding them from the profile instead would be a second answer to "what is this
    // profile's default", and two answers is the divergence that put this bug here.
    //
    // 🔴 *What that means, and Raymond confirmed it (2026-08-02) with the edge spelled out.* The Assistant
    // Profile set to `bypassPermissions` gives a session that can act across *every* workspace and is
    // asked about nothing: the SDK's own Allow/Deny is gone by the mode, and the cockpit's consent card can be
    // gone too through AC-575's bypass list. Together there is no gate left that asks anybody anything. That is
    // the operator's choice to make on their own machine, and it is written here rather than left to be read back
    // as an oversight — see the restart affordance (`RestartAsync`), which exists because
    // `bypassPermissions` can only be chosen at a start, so the choice would otherwise be unreachable
    // without closing the cockpit.
    //
    // The assistant's own standing instruction is written last, so it wins over anything the map happens to carry
    // on the same key. AC-594: what the operator typed is *added* to `AssistantSystemPrompt.Default` — replacing it
    // outright is the advanced choice on the Assistant Profile, because that default carries the language rule, the
    // speak-don't-write rule, the honesty clause and the whole permission paragraph.
    //
    // `sdkAsksPermission`/`consentCardAsks` default to "still asks" (AC-759) so every call site that does not name
    // a profile-specific gate state — every existing test but the two written for this ticket — keeps composing
    // the same instruction it always did. `EnsureStartedAsync` above is the one caller that knows better and says so.
    internal static IReadOnlyDictionary<string, string> _LaunchOptions(
        Cockpit.Core.Profiles.SessionProfile profile,
        bool replacesStandingInstruction,
        string? memory,
        string? currentState = null,
        bool sdkAsksPermission = true,
        bool consentCardAsks = true)
    {
        var options = profile.Defaults?.OptionDefaults is { Count: > 0 } defaults
            ? new Dictionary<string, string>(defaults, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        options[WellKnownPluginSessionOptions.AppendSystemPrompt] = AssistantStandingInstruction.Compose(
            profile.SystemPrompt, replacesStandingInstruction, memory, currentState, sdkAsksPermission, consentCardAsks);

        return options;
    }

    // Gate A (AC-759): whether the SDK still raises its own Allow/Deny for a session on this profile. Reads the
    // same option map `_LaunchOptions` seeds its dictionary from, and falls back to the same app floor
    // `StartConfiguredAsync` above is given — a profile that names no permission mode starts on that floor, so this
    // has to agree with it or the paragraph and the actual gate could name different states.
    internal static bool _SdkAsksPermission(Cockpit.Core.Profiles.SessionProfile profile)
    {
        var mode = profile.Defaults?.OptionDefaults is { } defaults
            && defaults.TryGetValue(WellKnownPluginSessionOptions.PermissionMode, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : SessionOptionCatalog.DefaultPermissionMode.Value;

        return !string.Equals(mode, SessionOptionCatalog.BypassPermissionModeValue, StringComparison.Ordinal);
    }

    private void _SetUnavailable(string reason)
    {
        Activity = AssistantActivity.Unavailable;
        UnavailableReason = reason;
    }

    // Whether the instance is still usable. Asked of the session rather than remembered as a flag here: a runtime
    // can end without anything telling this class, which is exactly the quiet death that has to be noticed.
    private static bool _IsAlive(SessionViewModel session) => session.IsSessionReady;

    // A teardown failure must not become the caller's problem: the instance is already out of Session by the time
    // this runs, so the worst case is a runtime that outlives its reference — worth a log line, not an exception
    // thrown at a hotkey handler.
    private async Task _DisposeQuietlyAsync(SessionViewModel session)
    {
        // Before the dispose, and outside the try: the host wired this session up when it minted it, and that
        // wiring has to come off whether or not the runtime tears down cleanly — a dispose that throws would
        // otherwise leave the dead session subscribed for the life of the process.
        session.PropertyChanged -= _OnSessionPropertyChanged;
        session.Transcript.CollectionChanged -= _OnTranscriptChanged;
        _cockpit.ReleaseAssistantSession(session);

        // An unanswered consent card is answered here, and answered No. The broker has no timeout of its own
        // (CockpitViewModel's routing says so where it denies a second prompt for the same reason), so a card left
        // open on a session that is going away hangs its caller for the life of the process — and the caller is a
        // tool call that would otherwise still be waiting to act on the operator's behalf. Denied rather than
        // dropped: the operator restarted instead of clicking Allow, and an action nobody approved must not become
        // an action nobody refused either. Here rather than in the restart, so the replace-a-dead-instance path
        // gets it too — a session that fell over mid-prompt leaves exactly the same card behind.
        if (session.PendingConsent is { } consent)
        {
            consent.DenyCommand.Execute(null);
            session.PendingConsent = null;
        }

        try
        {
            await session.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The previous assistant session could not be disposed cleanly.");
        }
    }
}
