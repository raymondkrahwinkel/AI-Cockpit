using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Cockpit.Core.Configuration;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Assistant;

// AC-1013: Owns the voice assistant's own session (AC-543, decision 3) and keeps the only reference, starts it lazily
// and revives a dead one on the same conversation. AC-1379: drives it through `IAssistantSession`, so it runs without
// the app; the app hands it a presence that raises on the UI thread, the backend the plain one.
public sealed class AssistantSessionHost : IAssistantSessionHost, ISingletonService
{
    // AC-1013: Fixed pane id (not a fresh guid per launch) so the state store's last-conversation lookup keeps
    // matching across starts; also the identity the broad read tools check against (AC-544), kept in Core since
    // Infrastructure hosts those tools and two copies of a guardrail constant is one that can stop matching.
    internal const string AssistantPaneId = AssistantIdentity.PaneId;

    private readonly ISessionLauncher _launcher;
    private readonly INodeControllerPresence _presence;
    private readonly IAssistantSettingsStore _settings;
    private readonly IAssistantProfileStore _profiles;
    private readonly ISessionStateStore _sessionState;
    private readonly SessionStateRecorder _sessionStateRecorder;
    private readonly IMcpServerCatalog _mcpServers;
    private readonly IAssistantMemory _memory;
    private readonly ILogger<AssistantSessionHost> _logger;

    // Serializes starts: a hotkey hold and a chip click landing together must not each build an instance.
    private readonly SemaphoreSlim _startGate = new(1, 1);

    // AC-1382: the backend's exclusion over this host's state, as `SessionLauncher._gate` is. The MCP thread, the runtime
    // pump and the presence timer all reach it there. Sections are synchronous, so it is never held across an await.
    private readonly Lock _gate = new();

    // The properties a section changed, raised once it has let go of `_gate` (see `_RunExclusive`).
    private readonly List<string> _unraised = [];

    // AC-740: backs DefaultWorkingDirectory below. Lazily kicked off by that property's first read, not the
    // constructor — most windows never open the @-mention picker before a session starts.
    private string? _defaultWorkingDirectory;
    private Task? _defaultWorkingDirectoryLoad;

    public AssistantSessionHost(
        ISessionLauncher launcher,
        INodeControllerPresence presence,
        IAssistantSettingsStore settings,
        IAssistantProfileStore profiles,
        ISessionStateStore sessionState,
        SessionStateRecorder sessionStateRecorder,
        IMcpServerCatalog mcpServers,
        IAssistantMemory memory,
        ILogger<AssistantSessionHost> logger)
    {
        _launcher = launcher;
        _presence = presence;
        _settings = settings;
        _profiles = profiles;
        _sessionState = sessionState;
        _sessionStateRecorder = sessionStateRecorder;
        _mcpServers = mcpServers;
        _memory = memory;
        _logger = logger;

        // AC-1321: a controller appearing or going away is re-read through the same path a settings save takes,
        // so the takeover is one more reason on the existing off state and not a second one.
        _presence.Changed += (_, _) =>
        {
            _ApplyTurnHold();
            _ = ApplySettingsAsync();
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // The living assistant instance, or null while it has not been woken yet. The one reference there is.
    public IAssistantSession? Session
    {
        get => _session;
        set => _ReplaceSession(value);
    }

    private IAssistantSession? _session;

    // What the indicator reports. Fed from here rather than read off the session, because "off" and "never started" are states no session exists to report.
    public AssistantActivity Activity
    {
        get => _activity;
        private set => _Set(ref _activity, value);
    }

    private AssistantActivity _activity = AssistantActivity.Unavailable;

    // AC-1013: Why the assistant cannot be reached (off, no profile, or start failed), for the operator. Non-null
    // exactly while `Activity` is `AssistantActivity.Unavailable` — an unavailable chip that
    // doesn't say why sends someone into Options hunting for a setting that isn't the problem.
    public string? UnavailableReason
    {
        get => _unavailableReason;
        private set => _Set(ref _unavailableReason, value);
    }

    private string? _unavailableReason = "The assistant is switched off. Turn it on in Options → Assistant.";

    // AC-740: the picker's fallback working directory before a session exists. Lazily loaded on first read
    // (see the field above); the very first '@' before that resolves reads null, so the picker's own
    // null-workingDirectory guard just keeps it shut for that one instant.
    public string? DefaultWorkingDirectory
    {
        get
        {
            // ponytail: outside `_gate`; two first reads can both start the load, which only reads the profile.
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
            _Raise(nameof(DefaultWorkingDirectory));
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
    public void ReportHoldListening(bool listening) => _RunExclusive(() =>
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
    });

    // AC-1013: Speech-to-text working on what was just said (AC-543, 2026-08-08 — used to be a line on the shared
    // voice pill). Guarded like the hold above (never overwrites Unavailable); ends back to Ready, not whatever
    // came before, since Thinking (set by SendAsync) is a beat later and the chip must not sit on a stale state.
    public void ReportTranscribing(bool transcribing) => _RunExclusive(() =>
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
    });

    // The one-time model/runtime fetch in front of the first transcription. A step with no status ends it and
    // hands back to Transcribing — preparation always precedes an actual transcription, never a resting chip.
    public void ReportPreparing(string? status, double? fraction) => _RunExclusive(() =>
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
    });

    // What speech-to-text is fetching right now, and how far along it is where that is known — shown on the chip
    // beside `AssistantActivity.Preparing`. Null whenever nothing is being prepared.
    public string? PreparationStatus
    {
        get => _preparationStatus;
        private set => _Set(ref _preparationStatus, value);
    }

    private string? _preparationStatus;

    public double? PreparationProgress
    {
        get => _preparationProgress;
        private set => _Set(ref _preparationProgress, value);
    }

    private double? _preparationProgress;

    // AC-1013: Brings the assistant up if not already (idempotent, replaces a dead instance). Never throws — the
    // callers are hotkey/click handlers with nowhere to put an exception; a failed start instead leaves
    // `Activity` on `Unavailable` with the reason set, so the chip says what the log used to say alone.
    public Task<IAssistantSession?> EnsureStartedAsync(CancellationToken cancellationToken = default) =>
        _StartOrReplaceAsync(replaceALiveInstance: false, startFresh: false, cancellationToken);

    // AC-1013: Stands the assistant down and brings it straight back up on the same conversation, so a start-time
    // setting (e.g. `bypassPermissions`, choosable only at a start — bug #15) takes effect without closing the
    // cockpit. Keeps the conversation via the normal `_StartAsync`/`_ResolveResumeAsync` resume path, and reuses `_DisposeQuietlyAsync`.
    public Task<IAssistantSession?> RestartAsync(CancellationToken cancellationToken = default) =>
        _StartOrReplaceAsync(replaceALiveInstance: true, startFresh: false, cancellationToken);

    // AC-1261 criterion 1: the one entry point a clear runs through — the flyout row (which stops a running turn
    // first, criterion 6) and the `clear_conversation` tool's deferred execution (criterion 7) both call this and
    // nothing else. Same shape as AC-596's hand-over: `_StartAsync` remains the only place that archives.
    public Task<IAssistantSession?> ClearConversationAsync(CancellationToken cancellationToken = default) =>
        _StartOrReplaceAsync(
            replaceALiveInstance: true,
            startFresh: true,
            cancellationToken,
            startFreshBecause: "Conversation cleared — a new one starts here");

    // AC-1013: How full the context may get before the assistant hands itself over and restarts (AC-596) — a
    // percentage, not a token count, since the provider reports fill and knows the window.
    // ponytail: one number for every provider, add per-provider tuning if that measurably matters.
    internal const double RestartAboveContextPercent = 80;

    // AC-1013: `replaceALiveInstance` — whether a healthy instance is torn down too (false =
    // `EnsureStartedAsync`'s idempotent lazy start, true = `RestartAsync`); one body so both take the
    // same start gate. `startFresh` — resume the conversation (default) or not (AC-596's hand-over).
    private async Task<IAssistantSession?> _StartOrReplaceAsync(
        bool replaceALiveInstance,
        bool startFresh,
        CancellationToken cancellationToken,
        string? startFreshBecause = null)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            // AC-1321: no new turn while a controller holds the line — before the live-instance shortcut, since a
            // running conversation is allowed to finish its turn but not to take another.
            if (_presence.Current is { } controller)
            {
                _SetUnavailable(TakeoverReason(controller));
                return null;
            }

            if (!replaceALiveInstance && Session is { } live && _IsAlive(live))
            {
                return live;
            }

            // A dead instance is dropped before a new one is built, so a start that fails does not leave the
            // corpse in place looking reachable.
            if (_ReplaceSession(null) is { } previous)
            {
                _logger.LogInformation(
                    replaceALiveInstance
                        ? "Restarting the assistant session on the same conversation."
                        : "The assistant session had stopped; starting a new one on the same conversation.");
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
        _RunExclusive(() => Activity = AssistantActivity.Thinking);

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
            session.SubmitComposer();
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
            live.ApplySpeech(settings.SpeakReplies);
        }

        // AC-1327 criterion 1: a controller outranks every other unavailable reason, including the feature being
        // off — a connected node always has an assistant. Re-run on every transition, so falling back here
        // restores whichever reason applies without one.
        if (_presence.Current is { } controller)
        {
            _SetUnavailable(TakeoverReason(controller));
            return;
        }

        if (settings.IsEnabled)
        {
            // Deliberately does not start anything: switching the feature on makes the assistant available, and
            // the first hold or click is still what wakes it. A live session that was stood down for a controller
            // (AC-1321) comes back to what it is doing rather than to Ready.
            _RunExclusive(() =>
            {
                if (Session is null)
                {
                    Activity = AssistantActivity.Ready;
                    UnavailableReason = null;
                }
                else if (Activity == AssistantActivity.Unavailable)
                {
                    Activity = AssistantActivity.Ready;
                    UnavailableReason = null;
                    _SyncActivityWithSession(Session);
                }
            });

            return;
        }

        var stopping = _ReplaceSession(null);
        _SetUnavailable("The assistant is switched off. Turn it on in Options → Assistant.");

        if (stopping is not null)
        {
            await _DisposeQuietlyAsync(stopping).ConfigureAwait(true);
        }
    }

    private async Task<IAssistantSession?> _StartAsync(
        bool startFresh, CancellationToken cancellationToken, string? startFreshBecause = null)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(true);
        if (!settings.IsEnabled)
        {
            // Criterion 1: with the feature off the hotkey does nothing — and says why, rather than being a key
            // that quietly is not there.
            _SetUnavailable("The assistant is switched off. Turn it on in Options → Assistant.");
            return null;
        }

        var slot = await _profiles.LoadAsync(cancellationToken).ConfigureAwait(true);
        if (slot.Profile is not { } profile)
        {
            _SetUnavailable(slot.UnsetReason ?? "No Assistant Profile is set. Pick one in Options → Assistant.");
            return null;
        }

        var session = _launcher.CreateAssistantSession();
        if (session is null)
        {
            _SetUnavailable("This cockpit cannot start sessions.");
            return null;
        }

        _RunExclusive(() =>
        {
            Activity = AssistantActivity.Thinking;
            UnavailableReason = null;
        });

        // Picks up yesterday's conversation when there is one — the same resume the restore path uses, rather
        // than a retention rule invented here.
        var resume = startFresh
            ? SessionResume.New
            : await _ResolveResumeAsync(cancellationToken).ConfigureAwait(true);

        // AC-684: replay before the launch so the window shows the earlier conversation the moment it attaches,
        // not after — a resume the provider ends up refusing (below) throws this whole session away anyway.
        await session.PrepareRecordedTranscriptAsync(resume, cancellationToken).ConfigureAwait(true);

        // AC-1089: fixed rather than inherited from Environment.CurrentDirectory — an AppImage's mount folder is a
        // fresh random name every launch, so a saved conversation id resumed from there never matches the folder
        // Claude looks under next time. Beside Cockpit's own state (Raymond's choice), which also never moves.
        var workingDirectory = CockpitBuild.StateRoot;

        // Created here rather than assumed: spawning into a folder that does not exist yet fails the whole start
        // ("No such file or directory"), and nothing guarantees another writer reached this root first.
        Directory.CreateDirectory(workingDirectory);

        // AC-1013: the session's own defaults are only the floor — the profile's own permission mode/model/effort ride
        // the launch options below (the driver prefers those). See _LaunchOptions for what bypassPermissions means here.
        await session.StartAsync(new AssistantLaunch(
            profile,
            workingDirectory,
            resume,
            // The one place in the codebase that names the broad read server (AC-544). See _McpSelectionAsync.
            await _McpSelectionAsync(profile, cancellationToken).ConfigureAwait(true),
            _LaunchOptions(
                profile,
                slot.ReplacesStandingInstruction,
                await _memory.ReadAsync(AssistantMemoryScope.Behaviour, cancellationToken).ConfigureAwait(true),
                await _memory.ReadCurrentStateAsync(cancellationToken).ConfigureAwait(true),
                await _memory.ReadAsync(AssistantMemoryScope.Machine, cancellationToken).ConfigureAwait(true),
                _SdkAsksPermission(profile),
                // AC-1013: Gate B (AC-759) reads ConsentBypassAll alone, not the per-source lists — the paragraph
                // describes the general expectation, and the per-call `approval` field (AssistantAgentMcpTools)
                // corrects it when only one source was switched off individually.
                consentCardAsks: !settings.ConsentBypassAll),
            settings.ReadingLevel)).ConfigureAwait(true);

        // Dropped, never assigned to `Session`: that change rebuilt the Simple stand's chat view, whose attach started the assistant again — a loop per layout pass when the start cannot succeed.
        if (!_IsAlive(session))
        {
            var reason = session.FailureReason;
            _logger.LogWarning("The assistant session was not running right after its start: {Reason}", reason);
            await _DisposeQuietlyAsync(session).ConfigureAwait(true);
            _SetUnavailable($"The assistant could not start: {reason}");
            return null;
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
            permissionMode: SessionPermissionModes.Default,
            // AC-1261 criterion 3: `startFresh` covers both AC-596's hand-over and a clear, and neither changes
            // profile or working directory — the guard this forces needs telling, not inferring.
            forgetConversation: startFresh);

        // AC-638/AC-596: say why in the transcript, since the hand-over note only reaches the system prompt.
        // `startFreshBecause` lets AC-684's failed-resume recovery use its own reason instead of this default.
        if (startFresh)
        {
            session.AddDivider(startFreshBecause ?? "Context was full — a new conversation starts here, picked up from a short note");
        }
        else if (resume.Mode == SessionResumeMode.BySessionId)
        {
            // AC-684: watched rather than awaited — a refused resume surfaces as a normal-looking return from
            // StartConfiguredAsync, and blocking every successful resume on a grace window would tax the common case.
            _WatchForUnresolvableResume(session);
        }

        session.ApplySpeech(settings.SpeakReplies);

        // A new instance has a new context, and a provider that reports no fill until its first turn would otherwise
        // never take the below-the-line reset — leaving the ask spent before this conversation had used anything.
        _RunExclusive(() => _askedTheProviderToCompact = false);
        Session = session;

        // AC-1013: The wire that makes Thinking end — only the session knows when a turn finishes, not the host's
        // own hold/send/start/failure moments. Without it the chip is set on the way in but never on the way
        // out: every send after the first leaves it stuck on Thinking.
        session.StateChanged += _OnSessionStateChanged;

        _RunExclusive(() => _SyncActivityWithSession(session));
        return session;
    }

    // Turns speaking on or off on the live session, so the header toggle takes effect on the next reply rather
    // than at the next restart. Does not stop what is already playing — that is the toggle's own job, and it
    // already does it (AC-543 criterion 9: off breaks off mid-sentence).
    public void SetSpeakReplies(bool speak)
    {
        if (Session is { } session)
        {
            session.SpeakReplies(speak);
        }
    }

    // AC-1379: the session says which change it is — busy, waiting on the operator, or the fill moved. Its flag is what
    // the old property-name filter carried beyond "reconsider": whether this change brings a newly read fill.
    private void _OnSessionStateChanged(object? sender, bool fillWasJustRead)
    {
        if (sender is not IAssistantSession session)
        {
            return;
        }

        // The session refreshes the provider's limits after it has published IsBusy false, so the busy transition
        // arrives with the previous turn's figure still standing. Why that matters: _ShouldRelieveTheFullContext.
        var relieve = _RunExclusive(() =>
        {
            _SyncActivityWithSession(session);
            return _ShouldRelieveTheFullContext(session, fillWasJustRead);
        });

        // Not awaited: this runs off a state change with nowhere to put a failure, and both branches report their
        // own — _StartOrReplaceAsync leaves the chip unavailable with the reason on it, and a compaction that could
        // not be asked for falls through to that restart rather than being lost.
        if (relieve)
        {
            _ = _RelieveTheFullContextAsync(session);
        }

        // AC-1261 criterion 7: a queued clear_conversation request runs the moment this same gate opens.
        if (_RunExclusive(() => _TakePendingConversationClear(session)))
        {
            _ = ClearConversationAsync();
        }
    }

    // AC-1261 criterion 7 (V2): marks a request rather than clearing now — clearing mid-turn would tear down the
    // very session that is running this tool call, and drop the tool result the turn is waiting on. Returns false
    // when a request is already queued (idempotent second call within the same turn), true otherwise.
    public bool RequestConversationClear()
    {
        // AC-1382: queued and taken in one section; the pump taking it too between those two steps ran it twice.
        var (queued, clearNow) = _RunExclusive(() =>
        {
            if (_pendingConversationClear)
            {
                return (false, false);
            }

            _pendingConversationClear = true;
            return (true, Session is { } session && _TakePendingConversationClear(session));
        });

        if (clearNow)
        {
            _ = ClearConversationAsync();
        }

        return queued;
    }

    // Cleared once it runs, or the moment `Session` changes instance (see `_ReplaceSession` below) — a session
    // that dies before its turn ends must not leave a clear queued for whatever replaces it.
    private bool _pendingConversationClear;

    // The same gate `_ShouldRelieveTheFullContext` uses (`ShouldHandOver`), with the fill given as always-over-the-line
    // so busy and waiting-on-operator alone decide. Under `_gate`: true hands the one queued request to this caller.
    private bool _TakePendingConversationClear(IAssistantSession session)
    {
        if (!_pendingConversationClear || !ReferenceEquals(session, Session))
        {
            return false;
        }

        if (!ShouldHandOver(double.PositiveInfinity, session.IsBusy, session.IsWaitingOnOperator))
        {
            return false;
        }

        _pendingConversationClear = false;
        return true;
    }

    // Relieves a context that is nearly full (AC-596) — but only while nothing is running and nothing is waiting on
    // the operator: that permission row belongs to a session that would no longer exist to receive the answer.
    // AC-664: a provider that can summarise its own conversation is asked to, and the restart is what is left.
    private bool _ShouldRelieveTheFullContext(IAssistantSession session, bool fillWasJustRead)
    {
        if (!ReferenceEquals(session, Session))
        {
            return false;
        }

        // Once a compaction has been asked for, only a fresh reading may decide anything: its turn ends with the
        // pre-compaction fill still standing, and judged there the hand-over would throw away the very conversation
        // the compaction had just saved.
        if (_askedTheProviderToCompact && !fillWasJustRead)
        {
            return false;
        }

        // A fill that came back under the line re-arms the ask: this is the only place that can tell a compaction
        // that worked from one that did not, and the next crossing is a new episode rather than a repeat of this one.
        if (session.ContextUsedPercent < RestartAboveContextPercent)
        {
            _askedTheProviderToCompact = false;
            return false;
        }

        // AC-1321: a compaction is a turn too; under a controller it waits for the next reading, like everything else.
        return session.CanTakeAPrompt && ShouldHandOver(session.ContextUsedPercent, session.IsBusy, session.IsWaitingOnOperator);
    }

    // Whether the provider has already been asked to compact this fill. Without it, every property change above the
    // line would send another `/compact` at a provider that answered the first one with "nothing to compact" — and
    // the ask is what makes the fill move, so the condition that triggered it is still true when the reply lands.
    private bool _askedTheProviderToCompact;

    private async Task _RelieveTheFullContextAsync(IAssistantSession session)
    {
        // AC-1382: checked and set in one section, so two readings over the line cannot both ask.
        var ask = _RunExclusive(() =>
        {
            var first = session.SupportsContextCompaction && !_askedTheProviderToCompact;
            _askedTheProviderToCompact |= first;
            return first;
        });

        if (ask)
        {
            _logger.LogInformation(
                "The assistant's context is {Fill:0}% full; asking the provider to compact it.",
                session.ContextUsedPercent);

            if (await session.CompactContextAsync().ConfigureAwait(true))
            {
                // AC-638's divider, for the case that keeps the conversation. A compaction is otherwise invisible
                // here — the provider reports it as a system line the transcript does not render — so the assistant's
                // memory of the early part would quietly thin out with nothing to say that it had.
                session.AddDivider("Context was full — the conversation so far was summarised and continues here");
                return;
            }
        }

        if (session.SupportsContextCompaction)
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
    private void _SyncActivityWithSession(IAssistantSession session) =>
        // Either kind of waiting counts: the SDK's own permission row, and the cockpit's consent gate for a
        // host-side tool. Both stop the turn dead until somebody clicks, and the chip's job is to say so.
        Activity = ActivityFor(Activity, session.IsBusy, session.IsWaitingOnOperator);

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
    private void _WatchForUnresolvableResume(IAssistantSession session)
    {
        // A change to a row the replay put there is not a row arriving; the first new one decides.
        void OnRow(TranscriptRowUpsert upsert)
        {
            if (upsert.Version > 1)
            {
                return;
            }

            session.RowUpserted -= OnRow;

            if (upsert.Row.Kind == TurnCompletedKind && ReferenceEquals(session, Session))
            {
                _ = _RecoverFromUnresolvableResumeAsync(session, upsert.Row.Text);
            }
        }

        session.RowUpserted += OnRow;
    }

    // `TranscriptEntryKind.TurnCompleted` as a row snapshot spells it.
    private const string TurnCompletedKind = "TurnCompleted";

    // Drops the session whose resume the provider refused and starts over clean, the same replace-a-dead-instance
    // shape `_RelieveTheFullContextAsync` uses for AC-596's hand-over — but with its own reason on the divider
    // rather than that one's "context was full", so the operator reads what actually happened.
    private async Task _RecoverFromUnresolvableResumeAsync(IAssistantSession session, string reason)
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
        string? machineMemory = null,
        bool sdkAsksPermission = true,
        bool consentCardAsks = true)
    {
        var options = profile.Defaults?.OptionDefaults is { Count: > 0 } defaults
            ? new Dictionary<string, string>(defaults, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        options[WellKnownPluginSessionOptions.AppendSystemPrompt] = AssistantStandingInstruction.Compose(
            profile.SystemPrompt, replacesStandingInstruction, memory, currentState, machineMemory, sdkAsksPermission, consentCardAsks);

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
                : SessionPermissionModes.Default;

        return !string.Equals(mode, SessionPermissionModes.Bypass, StringComparison.Ordinal);
    }

    // AC-1321: what the screen says while a controller holds the line. Local clock, short — it is read by someone
    // sitting at this machine. Shared with the chat view model so a scene without this host says the same thing.
    public static string TakeoverReason(ActiveController controller) =>
        $"Controlled by {controller.Name} since {controller.SinceUtc.ToLocalTime():HH:mm}. "
        + "Your assistant here comes back by itself when that connection drops.";

    // AC-1321: the takeover reaches the live session as a hold on its turn funnel, not only as the chip's state —
    // `_StartOrReplaceAsync` guards a start, but an inbox wake or the send-queue starts a turn on a session that is
    // already running, and those go through the session, not through this host. Synchronous on purpose.
    private void _ApplyTurnHold()
    {
        if (Session is { } live)
        {
            live.TurnsHeldBecause = _presence.Current is { } controller ? TakeoverReason(controller) : null;
        }
    }

    private void _SetUnavailable(string reason) => _RunExclusive(() =>
    {
        Activity = AssistantActivity.Unavailable;
        UnavailableReason = reason;
    });

    // AC-1382: swaps the instance in one section and hands back the one it replaced, so two callers dropping it cannot
    // both get it to dispose. A clear queued for the old instance goes with it.
    private IAssistantSession? _ReplaceSession(IAssistantSession? value)
    {
        var previous = _RunExclusive(() =>
        {
            var current = _session;
            if (!ReferenceEquals(current, value))
            {
                _session = value;
                _pendingConversationClear = false;
            }

            return current;
        });

        if (ReferenceEquals(previous, value))
        {
            return previous;
        }

        // In CommunityToolkit's order: the change's own handling first, its announcement after. A session that arrives
        // while a controller already holds the line takes the hold with it, since nothing else would revisit it.
        _ApplyTurnHold();
        _Raise(nameof(Session));
        return previous;
    }

    // AC-1382: the F1 pattern (`SessionLauncher.RunExclusiveAsync`), private since nothing outside decides here. The
    // decision runs under `_gate`, what it changed is raised after, and the caller acts on its answer outside. Sections
    // do not nest: an inner one would raise while the outer still holds the lock.
    private T _RunExclusive<T>(Func<T> decision)
    {
        Debug.Assert(!_gate.IsHeldByCurrentThread, "Sections of the assistant host's gate do not nest.");
        T answer;
        string[] changed;
        lock (_gate)
        {
            answer = decision();
            changed = [.. _unraised];
            _unraised.Clear();
        }

        foreach (var name in changed)
        {
            _Raise(name);
        }

        return answer;
    }

    private void _RunExclusive(Action section) => _RunExclusive(() =>
    {
        section();
        return true;
    });

    // Whether the instance is still usable. Asked of the session rather than remembered as a flag here: a runtime
    // can end without anything telling this class, which is exactly the quiet death that has to be noticed.
    private static bool _IsAlive(IAssistantSession session) => session.IsSessionReady;

    // A teardown failure must not become the caller's problem: the instance is already out of Session by the time
    // this runs, so the worst case is a runtime that outlives its reference — worth a log line, not an exception
    // thrown at a hotkey handler.
    private async Task _DisposeQuietlyAsync(IAssistantSession session)
    {
        // Before the dispose, and outside the try: the host wired this session up when it minted it, and that
        // wiring has to come off whether or not the runtime tears down cleanly — a dispose that throws would
        // otherwise leave the dead session subscribed for the life of the process.
        session.StateChanged -= _OnSessionStateChanged;
        _launcher.ReleaseAssistantSession(session);

        // AC-1013: An unanswered consent card is answered here, and answered No — the broker has no timeout of
        // its own, so a card left open would hang its tool call for the life of the process. Denied, not dropped:
        // an action nobody approved must not become one nobody refused either. Done here (not in the restart) so the replace-a-dead-instance path gets it too.
        session.DenyPendingConsent();

        try
        {
            await session.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The previous assistant session could not be disposed cleanly.");
        }
    }

    // Under `_gate`: the change is queued and `_RunExclusive` raises it once the section has let go.
    private void _Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        Debug.Assert(_gate.IsHeldByCurrentThread, "The assistant host's state is set inside a section of its gate.");
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        _unraised.Add(name);
    }

    private void _Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
