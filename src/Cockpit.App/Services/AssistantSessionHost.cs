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
using Cockpit.Core.Voice;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Services;

/// <summary>
/// Spins up the voice assistant's own session and owns it (AC-543, decision 3).
/// </summary>
/// <remarks>
/// <b>Why the host makes it.</b> The assistant gets to see across every workspace, which is a level of reach no
/// ordinary session has. If it were started the way other sessions are — through the delegation path, or as a pane
/// — then "which session is the assistant" would be a claim something makes, and a claim can be made by anything
/// that learns to make it. Here the host builds the instance and keeps the only reference to it, so the answer is
/// settled by construction: <see cref="Session"/> <em>is</em> the assistant, and there is no sentence an agent can
/// say that puts it in this field.
/// <para>
/// <b>Lazily.</b> Nothing starts at app start — not on the first render, not on a timer. The first hold of the
/// assistant hotkey or the first click on the chip is what brings it up, so an operator who has the feature on but
/// never uses it pays for no model in memory and no session on a bill. The first-time wait is visible in the
/// indicator (<see cref="AssistantActivity.Thinking"/> while it comes up) rather than spent as silence.
/// </para>
/// <para>
/// <b>And it comes back.</b> A delegated task reaps itself when it is done; this does the opposite. A session that
/// falls over quietly is only discovered the next time you ask it something — the silence this product refuses
/// everywhere else — so <see cref="EnsureStartedAsync"/> notices a dead instance and stands a new one up in its
/// place, resuming the same conversation.
/// </para>
/// <para>
/// <b>The conversation outlives everything.</b> The pop-out window is a view onto this session, never its owner:
/// closing it leaves the instance running. Across a restart the thread is picked up the way every other session
/// does it (AC-409/AC-410) — the state store's last record for <see cref="AssistantPaneId"/> names the
/// conversation, and the start resumes it. No separate retention rule of its own, deliberately: this surface is
/// the audit trail, and one that emptied on every restart would protect nothing.
/// </para>
/// <para>
/// It implements <see cref="IAssistantSessionHost"/> only so the chat window can be built against something a
/// test and the screenshotter can stand in for — this class needs a whole <c>CockpitViewModel</c> behind it. The
/// interface is not a seam for a second implementation: there is one assistant, and that there is exactly one is
/// the point.
/// </para>
/// </remarks>
public sealed partial class AssistantSessionHost : ObservableObject, ISingletonService, IAssistantSessionHost
{
    /// <summary>
    /// The pane id the assistant is always known by. Fixed rather than a fresh guid per launch: the state store
    /// keys the last conversation on the pane, so an id that changed every start would leave yesterday's
    /// conversation on disk under a name nothing looks up again.
    /// <para>
    /// Now also the identity the broad read tools check against (AC-544), which is why the value itself lives in
    /// Core: Infrastructure hosts those tools and cannot see this assembly, and two copies of a guardrail's
    /// constant is a guardrail that can quietly stop matching.
    /// </para>
    /// </summary>
    internal const string AssistantPaneId = AssistantIdentity.PaneId;

    private readonly CockpitViewModel _cockpit;
    private readonly IAssistantSettingsStore _settings;
    private readonly IAssistantProfileStore _profiles;
    private readonly ISessionStateStore _sessionState;
    private readonly IMcpServerCatalog _mcpServers;
    private readonly ILogger<AssistantSessionHost> _logger;

    /// <summary>Serializes starts: a hotkey hold and a chip click landing together must not each build an instance.</summary>
    private readonly SemaphoreSlim _startGate = new(1, 1);

    public AssistantSessionHost(
        CockpitViewModel cockpit,
        IAssistantSettingsStore settings,
        IAssistantProfileStore profiles,
        ISessionStateStore sessionState,
        IMcpServerCatalog mcpServers,
        ILogger<AssistantSessionHost> logger)
    {
        _cockpit = cockpit;
        _settings = settings;
        _profiles = profiles;
        _sessionState = sessionState;
        _mcpServers = mcpServers;
        _logger = logger;
    }

    /// <summary>The living assistant instance, or null while it has not been woken yet. The one reference there is.</summary>
    [ObservableProperty]
    private SessionViewModel? _session;

    /// <summary>What the indicator reports. Fed from here rather than read off the session, because "off" and "never started" are states no session exists to report.</summary>
    [ObservableProperty]
    private AssistantActivity _activity = AssistantActivity.Unavailable;

    /// <summary>
    /// Why the assistant cannot be reached, in words for the operator — the feature is off, no profile is set, or
    /// the start failed. Non-null exactly while <see cref="Activity"/> is <see cref="AssistantActivity.Unavailable"/>:
    /// an unavailable chip that does not say why sends someone into Options looking for a setting that is not the
    /// problem.
    /// </summary>
    [ObservableProperty]
    private string? _unavailableReason = "The assistant is switched off. Turn it on in Options → Voice.";

    /// <summary>
    /// The Assistant Profile's provider/model, formatted with <see cref="ProfileDisplay.Format"/> — the same
    /// convention every other profile picker in the app already uses, rather than a bespoke string invented for
    /// this one chip. Fed to the indicator as its Ready-state subtitle (AC-543 vormgeving pass, criterion 3: the
    /// question a Ready chip answers is "which model am I about to talk to"). Null until a profile has actually
    /// been read — an unset/unreadable profile leaves the chip with no subtitle rather than a stale one.
    /// </summary>
    [ObservableProperty]
    private string? _profileLabel;

    /// <summary>
    /// The assistant hotkey went down or came back up. Reported here rather than left for the indicator to infer
    /// from the shared voice pill — see <see cref="IAssistantSessionHost.ReportHoldListening"/> for what that
    /// inference got wrong.
    /// </summary>
    /// <remarks>
    /// Only moves between Ready and Listening: a hold that ends hands over to <see cref="SendAsync"/>, which sets
    /// Thinking, and neither may overwrite an Unavailable the operator still needs to read.
    /// </remarks>
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

    /// <summary>
    /// Brings the assistant up if it is not already, and returns it. Idempotent, and the recovery path too: an
    /// instance that died is replaced rather than handed back dead.
    /// </summary>
    /// <remarks>
    /// Never throws. Its callers are a hotkey handler and a click handler, neither of which has anywhere to put an
    /// exception — and what a swallowed one would take with it is the assistant, silently. A failed start leaves
    /// <see cref="Activity"/> on <see cref="AssistantActivity.Unavailable"/> with the reason set, which is the
    /// chip saying out loud what the log used to say alone.
    /// </remarks>
    public async Task<SessionViewModel?> EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (Session is { } live && _IsAlive(live))
            {
                return live;
            }

            // A dead instance is dropped before a new one is built, so a start that fails does not leave the
            // corpse in place looking reachable.
            if (Session is { } dead)
            {
                _logger.LogInformation("The assistant session had stopped; starting a new one on the same conversation.");
                Session = null;
                await _DisposeQuietlyAsync(dead).ConfigureAwait(true);
            }

            return await _StartAsync(cancellationToken).ConfigureAwait(true);
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

    /// <summary>
    /// Sends one utterance or typed line to the assistant, starting it first if this is the first time. The single
    /// entry point for both input paths, so speaking and typing reach the same conversation by the same route —
    /// which is what makes the assistant fully usable with no microphone at all.
    /// </summary>
    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
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

        session.InjectAndSubmit(text.Trim());
    }

    /// <summary>
    /// Re-reads the settings and stands the assistant down if the feature was switched off — including mid-sentence,
    /// which is the point: whoever clicks off wants silence, not one more paragraph.
    /// </summary>
    public async Task ApplySettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(true);
        if (settings.IsEnabled)
        {
            // Deliberately does not start anything: switching the feature on makes the assistant available, and
            // the first hold or click is still what wakes it.
            if (Session is null)
            {
                Activity = AssistantActivity.Ready;
                UnavailableReason = null;
                // Reading the profile for display is not starting anything — no session, no model in memory —
                // so it does not compromise the lazy start above; it only lets an idle Ready chip say which
                // model it would talk to instead of leaving that blank until the first use.
                await _RefreshProfileLabelAsync(cancellationToken).ConfigureAwait(true);
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

    private async Task<SessionViewModel?> _StartAsync(CancellationToken cancellationToken)
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
        ProfileLabel = ProfileDisplay.Format(profile.Label, profile.Provider, ProfileDisplay.ModelOf(profile));

        await session.StartConfiguredAsync(
            profile,
            SessionOptionCatalog.DefaultPermissionMode,
            SessionOptionCatalog.DefaultModel,
            SessionOptionCatalog.DefaultEffort,
            // Picks up yesterday's conversation when there is one — the same resume the restore path uses, rather
            // than a retention rule invented here.
            resume: await _ResolveResumeAsync(cancellationToken).ConfigureAwait(true),
            // The one place in the codebase that names the broad read server (AC-544). See _McpSelectionAsync.
            enabledMcpServerNames: await _McpSelectionAsync(profile, cancellationToken).ConfigureAwait(true),
            launchOptions: _LaunchOptions(profile)).ConfigureAwait(true);

        _ApplySpeech(session, settings);
        Session = session;

        // The wire that makes Thinking end. Everything else here sets Activity at a moment the host knows about —
        // a hold, a send, a start, a failure — and none of those is the moment a turn finishes, because only the
        // session knows that. Without this the chip is written to on the way in and never on the way out: the
        // first send lands on Ready (set two lines up, after the send) while the assistant is plainly thinking,
        // and every send after that leaves it on Thinking for good, because EnsureStartedAsync returns a live
        // instance without touching Activity. Both are the same missing subscription rather than two bugs.
        session.PropertyChanged += _OnSessionPropertyChanged;
        _SyncActivityWithSession(session);
        return session;
    }

    /// <summary>
    /// Gives the assistant's session what it needs to actually be heard — decision 2's "TTS erna", which nothing
    /// was doing.
    /// </summary>
    /// <remarks>
    /// <b>Why it has to be done here at all.</b> The cockpit pushes the voice settings to its sessions by walking
    /// <c>Sessions</c>, and the assistant is deliberately not in that collection — it has no pane. So it kept the
    /// bare defaults: <c>ReadResponsesAloud</c> false, which makes the read-aloud flush return before it speaks a
    /// word, and <c>ReadAloudLanguage</c> "en", which would have read Dutch replies in an English voice. The
    /// operator heard nothing at all and there was nothing on screen to say why.
    /// <para>
    /// <b>Verbatim, and stated rather than left to the default.</b> <see cref="ReadAloudMode.Naturalized"/> and
    /// <see cref="ReadAloudMode.Summarized"/> both send the reply through the local-LLM cleanup service first, and
    /// AC-542 decision 10 is explicit that the assistant's words go out one-to-one: what shortens a 300-word answer
    /// is <see cref="AssistantSystemPrompt.Default"/>, not a rewrite afterwards. That is also why this is set on the
    /// session rather than followed from the operator's global read-aloud mode — their choice there is about
    /// sessions, and picking Summarized for those must not quietly put an LLM back in the assistant's path. The
    /// mode's own default happens to be Verbatim today; written out anyway, because a default that changes
    /// elsewhere would reintroduce the pipeline this epic is removing, silently.
    /// </para>
    /// <para>
    /// The voice and language do follow the operator's settings, read off the cockpit's already-resolved
    /// selections — the same values the fan-out to ordinary sessions uses, rather than a second copy of them.
    /// </para>
    /// </remarks>
    private void _ApplySpeech(SessionViewModel session, AssistantSettings settings)
    {
        session.ReadAloudMode = ReadAloudMode.Verbatim;

        // The second door to the same local model, and the one that is easy to miss: the turn acknowledgement
        // (AC-99) has a LocalLlm mode that writes its short "let me look" line with the cleanup service. Pinned to
        // the preset phrases — instant, no model call — so no local model is ever reached from here either.
        // Today the assistant keeps this value only because the operator's choice is fanned out by walking
        // `Sessions`, which it is not in; that is an accident of where it sits, not a rule, and it would stop
        // protecting anything the day someone adds the assistant to that loop. Raymond, 2026-08-01: a local model
        // is the operator's to choose as the Assistant Profile and nowhere else — never machinery underneath it.
        session.TurnAckMode = TurnAckMode.InstantPhrases;

        // One synthesis for the whole reply instead of one per sentence. Measured on this machine, sentence-by-
        // sentence spent about as long synthesising as speaking, so every full stop came with an audible hole in
        // it — for a surface whose entire output is speech, that is not a rough edge but the product.
        session.ReadAloudAsOneUtterance = true;

        session.TtsVoiceSid = _cockpit.SelectedTtsVoice.Sid;
        session.ReadAloudLanguage = _cockpit.SelectedReadAloudLanguage.Code;
        session.ReadResponsesAloud = settings.SpeakReplies;
    }

    /// <summary>
    /// Turns speaking on or off on the live session, so the header toggle takes effect on the next reply rather
    /// than at the next restart. Does not stop what is already playing — that is the toggle's own job, and it
    /// already does it (AC-543 criterion 9: off breaks off mid-sentence).
    /// </summary>
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
            && sender is SessionViewModel session)
        {
            _SyncActivityWithSession(session);
        }
    }

    /// <summary>
    /// Maps the session's own status onto what the chip reports.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow. It only ever moves between <see cref="AssistantActivity.Thinking"/> and
    /// <see cref="AssistantActivity.Ready"/>, and it refuses to speak over the two states the host owns and the
    /// session knows nothing about: <see cref="AssistantActivity.Unavailable"/> is a fact about the feature rather
    /// than about a turn, and <see cref="AssistantActivity.Listening"/> is a key being held right now — a turn
    /// completing mid-hold must not tell the operator the microphone closed.
    /// <para>
    /// Written as the set that means "working" rather than the set that means "done", so a status added later
    /// arrives as Ready and has to be argued into Thinking deliberately — the same direction
    /// <c>WorkspaceAgentGateway</c>'s wake check is written in, and for the same reason.
    /// </para>
    /// </remarks>
    private void _SyncActivityWithSession(SessionViewModel session) =>
        // Either kind of waiting counts: the SDK's own permission row, and the cockpit's consent gate for a
        // host-side tool. Both stop the turn dead until somebody clicks, and the chip's job is to say so.
        Activity = ActivityFor(Activity, session.IsBusy, session.HasPendingPermission || session.PendingConsent is not null);

    /// <summary>The rule itself, as a pure function so it can be asserted directly. Internal for that and no other caller.</summary>
    /// <remarks>
    /// <b>Both inputs are read raw, and neither is <see cref="SessionPanelViewModel.SessionStatus"/>.</b> That
    /// status is derived for a different audience and carries a deliberate stickiness this surface cannot use:
    /// <c>_needsAttention</c> is set when a prompt appears and cleared only when the operator sends their next
    /// message, and it outranks busy in the derivation. Reading it cost two wrong chips in a row — first stuck on
    /// "Needs you" long after the approval was given and the reply spoken, then, once the pending flag was read
    /// properly, stuck on "Ready" while the assistant was plainly working, because a session that still carries
    /// NeedsAttention never reports Busy at all. Right for a sidebar you are not looking at; useless for a chip
    /// that answers "what is it doing right now".
    /// <para>
    /// So: is a decision waiting, and is it working. Two facts, each read from where it actually lives.
    /// </para>
    /// </remarks>
    internal static AssistantActivity ActivityFor(
        AssistantActivity current, bool isBusy, bool hasPendingPermission) => current switch
    {
        AssistantActivity.Unavailable or AssistantActivity.Listening => current,
        // Ahead of busy: a session can still be working on something while it stands on a prompt, and what the
        // operator needs to know is the half they can act on.
        _ when hasPendingPermission => AssistantActivity.AwaitingOperator,
        _ => isBusy ? AssistantActivity.Thinking : AssistantActivity.Ready,
    };

    /// <summary>
    /// The conversation to pick up: the one the state store last recorded for this pane, or a fresh one when there
    /// is none (a first run, or a store that could not be read).
    /// </summary>
    private async Task<SessionResume> _ResolveResumeAsync(CancellationToken cancellationToken)
    {
        var states = await _sessionState.LoadAsync(cancellationToken).ConfigureAwait(true);
        return states.FirstOrDefault(state => string.Equals(state.PaneId, AssistantPaneId, StringComparison.Ordinal))
            is { ConversationId: { Length: > 0 } conversationId }
            ? SessionResume.BySessionId(conversationId)
            : SessionResume.New;
    }

    /// <summary>
    /// The MCP servers the assistant launches with: what it would have had anyway, plus the broad read server that
    /// only it may mount (AC-544, criterion 2).
    /// </summary>
    /// <remarks>
    /// <b>This is the mount rule.</b> <c>cockpit-assistant</c> is registered as an internal endpoint, which means it
    /// never reaches a session through the no-selection fan-out and never appears in a picker for anyone to tick —
    /// it is mounted only by a launch that names it, and this line is the only one that does. That is exclusion by
    /// construction rather than by permission check: the reason an ordinary session does not get these tools is that
    /// nothing hands them to it, not that something decided not to.
    /// <para>
    /// <b>Why the rest of the selection has to be spelled out.</b> Passing an explicit set overrides the profile's own
    /// saved one (<c>McpServerRegistryFilter.EffectiveSessionSelection</c>), and passing <em>only</em> the assistant
    /// server would therefore leave the assistant with nothing else — no Depot, no YouTrack, none of what the epic
    /// expects it to reach. So the profile's selection is carried through when it has one, and when it has none the
    /// set is what the no-selection fan-out would have given it: every enabled server that is a choice at all.
    /// <c>OfferedToOperator</c> is asked for that rather than a fourth hand-written copy of the same predicate — and
    /// asking it is also what keeps <em>other</em> internal endpoints out of this set. Widening one privileged
    /// launch into "and every internal endpoint too" is precisely the accident this rule exists to make impossible.
    /// </para>
    /// <para>
    /// A catalog that cannot be read is not a reason to start with a crippled assistant, but it is also not a reason
    /// to invent a selection: the failure is logged and the assistant launches with the broad server alone, which is
    /// the one thing this method is actually responsible for. Reporting less would be a silent downgrade.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// The selection itself, as a pure function of the profile and the catalog — so the rule that matters can be
    /// asserted directly rather than inferred from a started session. Internal for that test and for no other
    /// caller.
    /// </summary>
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
        };

        // A saved selection is the operator's own answer and is taken whole — including anything below, if they
        // ticked it deliberately. The filter is about what an assistant gets when nobody said.
        selection.UnionWith(profile.EnabledMcpServerNames
            ?? [.. McpServerRegistryFilter.OfferedToOperator(catalog)
                .Select(server => server.Name)
                .Where(name => !NotFannedOutToTheAssistant.Contains(name))]);
        return selection;
    }

    /// <summary>
    /// Servers the assistant is deliberately not handed by the no-selection fan-out (AC-545, Raymond 2026-08-01).
    /// </summary>
    /// <remarks>
    /// <b>One name, and the bar for a second one is high.</b> The assistant is meant to be over the whole cockpit
    /// (AC-545: "hij is overkoepelend over alles"), so a server is kept out only when there is something structural
    /// wrong with it being here — never because a category of tool feels like a lot of authority to hand to a voice.
    /// The first draft of this list also held the shell, containers, the cluster, worktrees, the repo's checks and
    /// its workflows, on that reasoning. It does not hold: every one of those raises its own Allow/Deny row in the
    /// chat window with the literal action spelled out, which is the same gate this ticket built for spawning. A
    /// tool that goes through the gate is not a reason to remove the tool.
    /// <para>
    /// <c>cockpit-orchestrator</c> is different in kind, not in degree. <c>delegate_task</c> starts real AI work with
    /// <em>no pane</em>: it appears in no roster, raises no Allow row of the kind this ticket built, and is written
    /// to no spawn trail — a second way to start work that goes around every guarantee AC-545 put in front of the
    /// first, rather than through them. Asked for "the same profile but as an SDK session" before
    /// <c>start_agent</c> had a route parameter, the assistant went looking for it there: a missing parameter and an
    /// open side door meet, and the guardrail is what loses.
    /// </para>
    /// <para>
    /// Still a default and not a boundary: an operator who wants it here ticks it on the Assistant Profile and gets
    /// it, selection and all. <c>AlwaysMounted</c> endpoints (<c>cockpit-session</c>, <c>cockpit-agents</c>) are not
    /// reachable from here by construction — they go to every session whatever any selection says.
    /// </para>
    /// </remarks>
    internal static readonly HashSet<string> NotFannedOutToTheAssistant = new(StringComparer.OrdinalIgnoreCase)
    {
        Cockpit.Core.Delegation.DelegationMcp.ServerName,
    };

    /// <summary>
    /// The assistant's standing instruction, on the launch option every provider honours. The profile's own system
    /// prompt wins when it has one — that is what "overridable per profile" means — and
    /// <see cref="AssistantSystemPrompt.Default"/> is what a profile that says nothing gets.
    /// </summary>
    private static IReadOnlyDictionary<string, string> _LaunchOptions(Cockpit.Core.Profiles.SessionProfile profile) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [WellKnownPluginSessionOptions.AppendSystemPrompt] =
                string.IsNullOrWhiteSpace(profile.SystemPrompt) ? AssistantSystemPrompt.Default : profile.SystemPrompt.Trim(),
        };

    private void _SetUnavailable(string reason)
    {
        Activity = AssistantActivity.Unavailable;
        UnavailableReason = reason;
        // No profile is confirmed running once the chip says Unavailable — carrying the last one forward would
        // outlive its truth the moment the assistant is switched off or the profile fails to load.
        ProfileLabel = null;
    }

    /// <summary>
    /// Reads the Assistant Profile purely for display — no session, no model load — so an idle Ready chip can
    /// name what it would talk to before the operator's first hold or click brings the assistant up.
    /// </summary>
    private async Task _RefreshProfileLabelAsync(CancellationToken cancellationToken)
    {
        var slot = await _profiles.LoadAsync(cancellationToken).ConfigureAwait(true);
        ProfileLabel = slot.Profile is { } profile
            ? ProfileDisplay.Format(profile.Label, profile.Provider, ProfileDisplay.ModelOf(profile))
            : null;
    }

    /// <summary>
    /// Whether the instance is still usable. Asked of the session rather than remembered as a flag here: a runtime
    /// can end without anything telling this class, which is exactly the quiet death that has to be noticed.
    /// </summary>
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
