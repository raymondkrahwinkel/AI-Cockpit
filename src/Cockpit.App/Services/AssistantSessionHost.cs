using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Cockpit.Core.Configuration;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Services;

// AC-1013: Spins up the voice assistant's own session and owns it (AC-543, decision 3) — builds it and keeps the
// only reference so "which session is the assistant" is settled by construction, starts lazily on first
// hotkey/click, revives a dead instance on the same conversation, implements `IAssistantSessionHost` only as a test seam.
public sealed partial class AssistantSessionHost : ObservableObject, ISingletonService, IAssistantSessionHost
{
    // AC-1013: Fixed pane id (not a fresh guid per launch) so the state store's last-conversation lookup keeps
    // matching across starts; also the identity the broad read tools check against (AC-544), kept in Core since
    // Infrastructure hosts those tools and two copies of a guardrail constant is one that can stop matching.
    internal const string AssistantPaneId = AssistantIdentity.PaneId;

    private readonly CockpitViewModel _cockpit;
    private readonly IAssistantSettingsStore _settings;
    private readonly IAssistantProfileStore _profiles;
    private readonly ISessionStateStore _sessionState;
    private readonly SessionStateRecorder _sessionStateRecorder;
    private readonly IMcpServerCatalog _mcpServers;
    private readonly IAssistantMemory _memory;
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
        SessionStateRecorder sessionStateRecorder,
        IMcpServerCatalog mcpServers,
        IAssistantMemory memory,
        ILogger<AssistantSessionHost> logger)
    {
        _cockpit = cockpit;
        _settings = settings;
        _profiles = profiles;
        _sessionState = sessionState;
        _sessionStateRecorder = sessionStateRecorder;
        _mcpServers = mcpServers;
        _memory = memory;
        _logger = logger;
    }

    // The living assistant instance, or null while it has not been woken yet. The one reference there is.
    [ObservableProperty]
    private SessionViewModel? _session;

    // What the indicator reports. Fed from here rather than read off the session, because "off" and "never started" are states no session exists to report.
    [ObservableProperty]
    private AssistantActivity _activity = AssistantActivity.Unavailable;

    // AC-1013: Why the assistant cannot be reached (off, no profile, or start failed), for the operator. Non-null
    // exactly while `Activity` is `AssistantActivity.Unavailable` — an unavailable chip that
    // doesn't say why sends someone into Options hunting for a setting that isn't the problem.
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


    // AC-1013: Hotkey down/up, reported here rather than inferred by the indicator from the shared voice pill (see
    // `IAssistantSessionHost.ReportHoldListening`). Only moves between Ready and Listening; a hold
    // ending hands off to `SendAsync` (which sets Thinking), and neither may overwrite Unavailable.
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

    // AC-1013: Speech-to-text working on what was just said (AC-543, 2026-08-08 — used to be a line on the shared
    // voice pill). Guarded like the hold above (never overwrites Unavailable); ends back to Ready, not whatever
    // came before, since Thinking (set by SendAsync) is a beat later and the chip must not sit on a stale state.
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

    // AC-1013: Brings the assistant up if not already (idempotent, replaces a dead instance). Never throws — the
    // callers are hotkey/click handlers with nowhere to put an exception; a failed start instead leaves
    // `Activity` on `Unavailable` with the reason set, so the chip says what the log used to say alone.
    public Task<SessionViewModel?> EnsureStartedAsync(CancellationToken cancellationToken = default) =>
        _StartOrReplaceAsync(replaceALiveInstance: false, startFresh: false, cancellationToken);

    // AC-1013: Stands the assistant down and brings it straight back up on the same conversation, so a start-time
    // setting (e.g. `bypassPermissions`, choosable only at a start — bug #15) takes effect without closing the
    // cockpit. Keeps the conversation via the normal `_StartAsync`/`_ResolveResumeAsync` resume path, and reuses `_DisposeQuietlyAsync`.
    public Task<SessionViewModel?> RestartAsync(CancellationToken cancellationToken = default) =>
        _StartOrReplaceAsync(replaceALiveInstance: true, startFresh: false, cancellationToken);

    // AC-1013: How full the context may get before the assistant hands itself over and restarts (AC-596) — a
    // percentage, not a token count, since the provider reports fill and knows the window.
    // ponytail: one number for every provider, add per-provider tuning if that measurably matters.
    internal const double RestartAboveContextPercent = 80;

    // AC-1013: `replaceALiveInstance` — whether a healthy instance is torn down too (false =
    // `EnsureStartedAsync`'s idempotent lazy start, true = `RestartAsync`); one body so both take the
    // same start gate. `startFresh` — resume the conversation (default) or not (AC-596's hand-over).
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
    public Task SendAsync(string text, CancellationToken cancellationToken = default) =>
        SendAsync(text, [], cancellationToken);

    public async Task SendAsync(string text, IReadOnlyList<byte[]> pngImages, CancellationToken cancellationToken = default)
    {
        // An image with no words is a message too (AC-630) — a pasted or captured attachment waiting on the
        // composer is reason enough to send, and refusing here left it hanging with no way out.
        if (string.IsNullOrWhiteSpace(text) && pngImages.Count == 0 && Session is not { HasPendingAttachments: true })
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

        // One notice, not one per attachment: AddPastedImage answers a provider that cannot see images with a
        // transcript row of its own (AC-1049), which a channel relays straight back to whoever sent the image.
        foreach (var image in session.CanPasteImages ? pngImages : pngImages.Take(1))
        {
            session.AddPastedImage(image);
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

        // AC-1013: Re-applies everything the start path applies from these settings, so a save reaches the running
        // assistant. Reuses the start path's own call rather than hand-picking fields — hand-picking is what
        // once left SpeakReplies behind while the header checkbox moved.
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
            await session.ReplayRecordedTranscriptAsync(cancellationToken).ConfigureAwait(true);
        }
        else
        {
            // AC-947/AC-1090: nothing will replay this log's rows into the new session, so roll it aside now,
            // while it still holds the conversation before this restart — a new conversation is a new log.
            await session.ArchiveRecordedTranscriptAsync(cancellationToken).ConfigureAwait(true);
        }

        // AC-1089: fixed rather than inherited from Environment.CurrentDirectory — an AppImage's mount folder is a
        // fresh random name every launch, so a saved conversation id resumed from there never matches the folder
        // Claude looks under next time. Beside Cockpit's own state (Raymond's choice), which also never moves.
        var workingDirectory = CockpitBuild.StateRoot;

        // Created here rather than assumed: spawning into a folder that does not exist yet fails the whole start
        // ("No such file or directory"), and nothing guarantees another writer reached this root first.
        Directory.CreateDirectory(workingDirectory);

        await session.StartConfiguredAsync(
            profile,
            // AC-1013: App defaults only as the floor — the profile's own permission mode/model/effort ride the
            // launch options below (the driver prefers those), so a profile that says nothing still starts as
            // before. See _LaunchOptions for the full rule and what bypassPermissions means here.
            SessionOptionCatalog.DefaultPermissionMode,
            SessionOptionCatalog.DefaultModel,
            SessionOptionCatalog.DefaultEffort,
            workingDirectory: workingDirectory,
            resume: resume,
            // The one place in the codebase that names the broad read server (AC-544). See _McpSelectionAsync.
            enabledMcpServerNames: await _McpSelectionAsync(profile, cancellationToken).ConfigureAwait(true),
            launchOptions: _LaunchOptions(
                profile,
                slot.ReplacesStandingInstruction,
                await _memory.ReadAsync(cancellationToken).ConfigureAwait(true),
                await _memory.ReadCurrentStateAsync(cancellationToken).ConfigureAwait(true),
                _SdkAsksPermission(profile),
                // AC-1013: Gate B (AC-759) reads ConsentBypassAll alone, not the per-source lists — the paragraph
                // describes the general expectation, and the per-call `approval` field (AssistantAgentMcpTools)
                // corrects it when only one source was switched off individually.
                consentCardAsks: !settings.ConsentBypassAll),
            readingLevel: settings.ReadingLevel).ConfigureAwait(true);

        // AC-1239: named against the assistant, since SessionViewModel's own warning says only which profile it was.
        if (!_IsAlive(session))
        {
            _logger.LogWarning("The assistant session was not running right after its start: {Reason}", session.StartFailure ?? session.Status);
        }

        // AC-1089: the assistant never came through here, so its record carried no ProfileId/WorkingDirectory and a
        // profile or working-directory switch read as "nothing changed", leaving a stale conversation id standing.
        // Fire-and-forget like the same call in CockpitViewModel — a started session must not wait on a state write.
        _ = _sessionStateRecorder.RecordSessionStartedAsync(
            AssistantPaneId,
            profile,
            workingDirectory,
            worktreePath: null,
            worktreeBranch: null,
            permissionMode: SessionOptionCatalog.DefaultPermissionMode.Value);

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

        // AC-1013: The wire that makes Thinking end — only the session knows when a turn finishes, not the host's
        // own hold/send/start/failure moments. Without it the chip is set on the way in but never on the way
        // out: every send after the first leaves it stuck on Thinking.
        session.PropertyChanged += _OnSessionPropertyChanged;

        _SyncActivityWithSession(session);
        return session;
    }

    // AC-1013: Seeds a starting session's speech (decision 2's "TTS erna") since nothing else does — otherwise a
    // session started after the last Options save keeps bare defaults (`ReadResponsesAloud` false,
    // `ReadAloudLanguage` "en"). Called from both start and `ApplySettingsAsync`; read-aloud is always verbatim (AC-542 decision 10, AC-546).
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

    // AC-1013: Maps the session's own status onto the chip. Only moves between Thinking and Ready — it never
    // overwrites Unavailable (a feature fact) or Listening (a key held right now). Written as the "working" set
    // rather than the "done" set, so a status added later defaults to Ready, not Thinking.
    private void _SyncActivityWithSession(SessionViewModel session) =>
        // Either kind of waiting counts: the SDK's own permission row, and the cockpit's consent gate for a
        // host-side tool. Both stop the turn dead until somebody clicks, and the chip's job is to say so.
        Activity = ActivityFor(Activity, session.IsBusy, session.HasPendingPermission || session.PendingConsent is not null);

    // AC-1013: Pure function so it can be asserted directly. Reads both inputs raw rather than
    // `SessionPanelViewModel.SessionStatus`, whose `_needsAttention` stickiness once produced two wrong chips
    // (stuck "Needs you", then stuck "Ready" while still working) — fine for a sidebar, wrong for a live chip.
    internal static AssistantActivity ActivityFor(
        AssistantActivity current, bool isBusy, bool hasPendingPermission) => current switch
    {
        AssistantActivity.Unavailable or AssistantActivity.Listening => current,
        // Ahead of busy: a session can still be working on something while it stands on a prompt, and what the
        // operator needs to know is the half they can act on.
        _ when hasPendingPermission => AssistantActivity.AwaitingOperator,
        _ => isBusy ? AssistantActivity.Thinking : AssistantActivity.Ready,
    };

    // AC-1013: The conversation to pick up — the state store's last record for this pane, or fresh when there is
    // none. Internal so the rule can be asserted directly; a restart's whole promise lives here.
    internal async Task<SessionResume> _ResolveResumeAsync(CancellationToken cancellationToken)
    {
        // AC-1089: TryLoadAsync, not LoadAsync — the latter turns a read failure into an empty list, indistinguishable
        // from "nothing was ever saved". That silently threw the assistant's conversation away on a transient read
        // error; a real failure is worth a log line even though a fresh start is the only option either way.
        var states = await _sessionState.TryLoadAsync(cancellationToken).ConfigureAwait(true);
        if (states is null)
        {
            _logger.LogWarning(
                "The assistant's saved session state could not be read; starting a new conversation instead of resuming.");
            return SessionResume.New;
        }

        return states.FirstOrDefault(state => string.Equals(state.PaneId, AssistantPaneId, StringComparison.Ordinal))
            is { ConversationId: { Length: > 0 } conversationId }
            ? SessionResume.BySessionId(conversationId)
            : SessionResume.New;
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

    // AC-1013: MCP servers the assistant launches with — the profile's own selection (or, if unset,
    // `OfferedToOperator`'s full fan-out set) plus the broad read server only this launch may mount (AC-544
    // criterion 2, exclusion by construction). A catalog read failure is logged; launch proceeds with the broad server alone.
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
        // AC-1013: Both of the assistant's own Internal endpoints, named here and nowhere else — read (AC-544) and
        // acting (AC-545). Both also check the caller's pane per tool, so this mount is gate one of two. Naming only
        // the read server would leave AC-545's tools registered but mounted nowhere, silently absent.
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

    // AC-1013: Servers deliberately not handed by the no-selection fan-out (AC-545). High bar — shell/containers/
    // cluster/worktrees/checks were dropped from an earlier draft since they raise their own Allow/Deny row.
    // `cockpit-orchestrator` stays out: `delegate_task` starts AI work with no pane/roster/spawn trail, a side door around AC-545. Still a default (operator can opt in on the profile), not a boundary.
    internal static readonly HashSet<string> NotFannedOutToTheAssistant = new(StringComparer.OrdinalIgnoreCase)
    {
        Cockpit.Core.Delegation.DelegationMcp.ServerName,
    };

    // AC-1013: Launch options = profile's `OptionDefaults` (carries permission mode/model/effort — without it an
    // operator's `bypassPermissions` used to reach the driver as "default", tool calls still asked about) plus
    // the standing instruction (AC-594, written last so it wins). With `bypassPermissions` both the SDK's own gate and (via AC-575) the consent card can be gone — the operator's own choice; hence `RestartAsync`.
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

    // AC-1013: Gate A (AC-759) — reads the same option map `_LaunchOptions` seeds from, falling back to the same
    // app floor `StartConfiguredAsync` uses, so the composed paragraph and the actual gate never disagree.
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
        _cockpit.ReleaseAssistantSession(session);

        // AC-1013: An unanswered consent card is answered here, and answered No — the broker has no timeout of
        // its own, so a card left open would hang its tool call for the life of the process. Denied, not dropped:
        // an action nobody approved must not become one nobody refused either. Done here (not in the restart) so the replace-a-dead-instance path gets it too.
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
