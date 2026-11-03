using Microsoft.Extensions.Logging.Abstractions;
using Avalonia.Threading;
using Cockpit.App.Composition;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Tests.Shared;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The assistant starts lazily, and when it will not start it says why (AC-543, criterion 1).
/// </summary>
/// <remarks>
/// The failure these guard against is not a crash but a silence: a hotkey that does nothing, on a feature the
/// operator believes is on. Every case therefore asserts the reason as well as the absence — an unavailable
/// assistant with no words is what sends someone into Options looking for a setting that is not the problem.
/// </remarks>
[Collection("avalonia")]
public class AssistantSessionHostTests
{
    private static void _AssertMachineMemorySuffix(string instruction) =>
        Assert.EndsWith($"{AssistantStandingInstruction.MachineMemoryHeading}\n\nnothing yet", instruction, StringComparison.Ordinal);
    [Fact]
    public void Send_WithTheFeatureOff_DoesNotStartTheAssistant()
    {
        var host = Dispatcher.UIThread.Invoke(() => _Host(enabled: false, slot: _ConfiguredSlot()));

        Dispatcher.UIThread.Invoke(() => host.SendAsync("what is the status of AC-223").GetAwaiter().GetResult());

        Assert.Null(host.Session);
        Assert.Equal(AssistantActivity.Unavailable, host.Activity);
    }

    // ── The chip's Thinking state has to end, and only the session knows when ──────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActivityFor_WhileAPermissionIsPending_SaysItNeedsYou_BusyOrNot(bool isBusy)
    {
        Assert.Equal(
            AssistantActivity.AwaitingOperator,
            AssistantSessionHost.ActivityFor(AssistantActivity.Thinking, isBusy, hasPendingPermission: true));
    }

    // ── AC-544 criterion 2, the mounting half: the assistant's launch is the one that names the broad read server ──

    // AC-766: this is the mount-side half of the fix — a plugin-provided server the catalog now offers unscoped
    // (Depot, say) is not marked ProjectLinked there (no single project points at it in that call), and the
    // no-selection fan-out must carry it through anyway; ProjectLinked is not one of the things it checks.
    [Fact]
    public void McpSelection_WithAnEmptySavedSelection_LeavesOutOrdinaryServers()
    {
        var selection = AssistantSessionHost.McpSelection(_Profile() with { EnabledMcpServerNames = [] }, [
            new McpServerConfig { Name = "Depot: wispslate", Enabled = true, ProjectLinked = false },
        ]);

        Assert.DoesNotContain("Depot: wispslate", selection);
    }

    /// <summary>
    /// The servers the assistant is not handed by default stay out of the fan-out: the orchestrator, another spawn's
    /// Internal endpoints, and anything switched off.
    /// </summary>
    /// <remarks>
    /// <c>cockpit-orchestrator</c> is the one this is really about: <c>delegate_task</c> starts AI work with no pane,
    /// no Allow row and no line in the spawn trail, which is every guarantee AC-545 built, absent.
    /// </remarks>
    [Fact]
    public void McpSelection_WithNoSavedSelection_LeavesOutTheServersTheAssistantIsNotGivenByDefault()
    {
        McpServerConfig[] catalog =
        [
            new() { Name = "cockpit-orchestrator", Enabled = true },
            new() { Name = "cockpit-elevated", Enabled = true, Internal = true },
            new() { Name = "autopilot-plan", Enabled = true, Internal = true },
            new() { Name = "off-server", Enabled = false },
            new() { Name = "depot", Enabled = true },
            new() { Name = "youtrack", Enabled = true, ProjectLinked = false },
        ];

        var selection = AssistantSessionHost.McpSelection(_Profile(), catalog);

        Assert.DoesNotContain("cockpit-orchestrator", selection);
        Assert.DoesNotContain("cockpit-elevated", selection);
        Assert.DoesNotContain("autopilot-plan", selection);
        Assert.DoesNotContain("off-server", selection);

        Assert.Contains("depot", selection);
        Assert.Contains("youtrack", selection);
    }

    // ── The assistant runs on its own profile's start defaults, not on the app's ──────────────────────────────
    //
    // The measured failure: the operator put bypassPermissions on the Assistant Profile and was still asked to
    // confirm every tool call. A plugin profile carries its permission mode, model and effort in the generic
    // OptionDefaults map — not in the legacy typed Defaults.PermissionMode/Model/Effort, which exist only for the
    // one-time migration — and that map reaches a driver as launch options. The assistant's launch built a map
    // holding nothing but its system prompt, so the profile was passed along and then, on the three settings that
    // decide what it may do, ignored.

    // ── AC-759: the acting paragraph's SDK gate agrees with the profile's own permission mode ────────────────────

    [Fact]
    public void SdkAsksPermission_TrueForAProfileThatNamesNoPermissionMode()
    {
        // The app floor (SessionOptionCatalog.DefaultPermissionMode) is a confining mode, so a profile that says
        // nothing still asks — the same fallback StartConfiguredAsync's own typed argument falls back to.
        Assert.True(AssistantSessionHost._SdkAsksPermission(_Profile()));
    }

    [Fact]
    public void LaunchOptions_TheAssistantsOwnInstruction_WinsOverAnythingTheProfileStoredOnThatKey()
    {
        // The profile's start defaults are copied first and the standing instruction is written last, on purpose:
        // this is the assistant, and a stored value on the host's own append-system-prompt key must not be able to
        // replace what makes it one.
        var profile = _ProfileWithDefaults((WellKnownPluginSessionOptions.AppendSystemPrompt, "you are a teapot"))
            with
        { SystemPrompt = "You are Olaf." };

        var options = AssistantSessionHost._LaunchOptions(profile, replacesStandingInstruction: false, memory: null);

        var instruction = options[WellKnownPluginSessionOptions.AppendSystemPrompt];
        Assert.DoesNotContain("teapot", instruction, StringComparison.Ordinal);
        Assert.Contains("You are Olaf.", instruction, StringComparison.Ordinal);
        _AssertMachineMemorySuffix(instruction);
    }

    // ── Handing over before the context fills (AC-596) ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(79.9, false, false, false)]
    [InlineData(80d, false, false, true)]
    [InlineData(99d, false, false, true)]
    // Never mid-turn: the restart would take the turn it is in the middle of with it.
    [InlineData(99d, true, false, false)]
    // Never over a permission nobody has answered: that row belongs to a session that would no longer exist.
    [InlineData(99d, false, true, false)]
    // No reading this turn is not an empty context; this row only pins that null never hands over.
    [InlineData(null, false, false, false)]
    public void HandingOver_WaitsForAContextThatIsFull_AndForAnAssistantThatIsDoingNothing(
        double? fill, bool isBusy, bool isWaitingOnOperator, bool expected) =>
        Assert.Equal(expected, AssistantSessionHost.ShouldHandOver(fill, isBusy, isWaitingOnOperator));

    // ── Compacting instead of throwing the conversation away (AC-664) ─────────────────────────────────────────

    /// <summary>
    /// AC-664: the hand-over above is data loss — the transcript goes and only the standing instruction, the memory
    /// file and the last <c>note_state</c> carry across. A provider that can summarise its own conversation is asked
    /// to do that instead, and the instance stays exactly where it was.
    /// </summary>
    [Fact]
    public void AFullContext_OnAProviderThatCanCompactItself_IsCompacted_RatherThanRestartedOnAFreshConversation()
    {
        var (host, first, driver) = _StartedAssistantOn(_CompactingProvider(), contextCompactionPercent: 30);

        _ReportAFullContext(first, driver, fill: 30);

        // The same conversation, and the ask that keeps it: a hand-over would have swapped the instance for one whose
        // transcript is empty.
        Assert.Same(first, host.View());
        driver.Received(1).CompactContextAsync(Arg.Any<CancellationToken>());

        // And it says so where the operator reads, since the provider reports a compaction nowhere they can see.
        var divider = Assert.Single(first.Transcript, entry => entry.IsDivider);
        Assert.Contains("continues here", divider.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ordering that made the first cut of this wrong: a turn publishes <c>IsBusy</c> false before re-reading the
    /// provider's limits, so the compaction turn ends with the old fill still standing. Judged there, the assistant
    /// is idle above the line and would hand over the conversation the compaction had just saved.
    /// </summary>
    [Fact]
    public void TheCompactionTurnEnding_IsNotJudgedOnTheFillFromBeforeIt()
    {
        var (host, first, driver) = _StartedAssistantOn(_CompactingProvider());

        _ReportAFullContext(first, driver, fill: 90);
        Assert.Same(first, host.View());

        // The compaction turn ends: busy drops while the provider's figure has not been re-read yet.
        Dispatcher.UIThread.Invoke(() => first.IsBusy = false);

        Assert.Same(first, host.View());
        driver.Received(1).CompactContextAsync(Arg.Any<CancellationToken>());
    }

    // ── Redrawing the transcript after a resume (AC-684) ──────────────────────────────────────────────────────

    /// <summary>
    /// The provider resumes its own memory already (`_ResolveResumeAsync`'s `BySessionId`); this is the other half
    /// that was missing — nothing repainted the window. What the store held before this launch is what the
    /// operator sees the moment the session comes up, in order, dividers included.
    /// </summary>
    [Fact]
    public void ResumingByConversationId_ReplaysThePersistedTranscript_BeforeTheOperatorSeesAnEmptyWindow()
    {
        var sessionState = Substitute.For<ISessionStateStore>();
        sessionState.TryLoadAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<SessionStateRecord>?>(_ =>
            [_StateFor(AssistantSessionHost.AssistantPaneId, "conv-1")]);

        var transcript = Substitute.For<ISessionTranscriptStore>();
        transcript.TryLoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<IReadOnlyList<TranscriptSnapshotEntry>?>(_ =>
        [
            _Recorded("UserText", "fix the layout bug"),
            _Recorded("Divider", "Context was full — a new conversation starts here"),
        ]);

        var (_, session, _) = _StartedAssistantOn(SessionCapabilities.ClaudeCli, sessionState, transcript);

        Assert.Equal(2, session.Transcript.Count);
        Assert.Equal(TranscriptEntryKind.UserText, session.Transcript[0].Kind);
        Assert.Equal("fix the layout bug", session.Transcript[0].Text);
        Assert.True(session.Transcript[1].IsDivider);
    }

    /// <summary>
    /// A row this build does not recognise — an older or newer snapshot shape — is skipped rather than guessed at,
    /// the same contract <c>SessionStateStore</c> uses for a line it cannot parse. The rest of the transcript still
    /// comes back.
    /// </summary>
    [Fact]
    public void AnUnrecognisedSavedRow_IsSkipped_RatherThanFailingTheWholeReplay()
    {
        var sessionState = Substitute.For<ISessionStateStore>();
        sessionState.TryLoadAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<SessionStateRecord>?>(_ =>
            [_StateFor(AssistantSessionHost.AssistantPaneId, "conv-1")]);

        var transcript = Substitute.For<ISessionTranscriptStore>();
        transcript.TryLoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<IReadOnlyList<TranscriptSnapshotEntry>?>(_ =>
        [
            _Recorded("SomeFutureKind", "from a newer build"),
            _Recorded("UserText", "still readable"),
        ]);

        var (_, session, _) = _StartedAssistantOn(SessionCapabilities.ClaudeCli, sessionState, transcript);

        var entry = Assert.Single(session.Transcript);
        Assert.Equal("still readable", entry.Text);
    }

    /// <summary>
    /// Criterion 4: a conversation id the provider no longer recognises (expired, stopped, unknown) surfaces as an
    /// immediate failed turn (AC-539's <c>error_during_execution</c>), not an exception. Silence here would be the
    /// operator staring at an empty window with no idea why — this recovers onto a fresh conversation and says so.
    /// </summary>
    [Fact]
    public void AnUnresolvableResume_RecoversOntoAFreshConversation_WithAReadableDividerExplainingWhy()
    {
        var sessionState = Substitute.For<ISessionStateStore>();
        sessionState.TryLoadAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<SessionStateRecord>?>(_ =>
            [_StateFor(AssistantSessionHost.AssistantPaneId, "gone")]);

        var (host, first, _) = _StartedAssistantOn(SessionCapabilities.ClaudeCli, sessionState);

        // The signal production reads (AC-539): the very first thing this fresh launch's transcript receives is a
        // failed turn — nothing here has sent the provider a prompt yet, so nothing else could have produced one.
        Dispatcher.UIThread.Invoke(() => first.Apply(new TurnCompleted
        {
            SessionId = "gone",
            Subtype = "error_during_execution",
            Result = null,
            IsError = true,
            Errors = ["No conversation found with session ID: gone"],
        }));

        var second = _ReplacementOf(host, first);
        var divider = Assert.Single(second.Transcript, entry => entry.IsDivider);
        Assert.Contains("Could not resume the previous conversation", divider.Text, StringComparison.Ordinal);
        Assert.Contains("error_during_execution", divider.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(second.Transcript, entry => !entry.IsDivider);
    }

    /// <summary>
    /// Criterion 2 (AC-947): a launch that will not replay the saved rows into the new session — no resumable
    /// conversation id — archives the file that held them first, before the first new row can overwrite it.
    /// AC-1994: the same start carries the slot's brain to the driver exactly once, ahead of a standing instruction
    /// it leaves as it was, and alone under its own key for a provider without a system prompt.
    /// </summary>
    [Fact]
    public void StartingWithNoResumableConversation_ArchivesTheSavedTranscript()
    {
        var transcript = Substitute.For<ISessionTranscriptStore>();
        const string Brain = "acme/zyra@depot.acme.example";
        var profile = _Profile() with { Assistant = "depot:" + Brain, SystemPrompt = "Your name is Zyra." };
        var sources = Substitute.For<IProjectMemorySourceRegistry>();
        sources.Sources.Returns([new ProjectMemorySourceRegistration("depot", "Depot project", "Read it.") { BrainInstruction = value => $"Bootstrap {value}." }]);

        var (_, _, driver) = _StartedAssistantOn(
            SessionCapabilities.ClaudeCli, transcript: transcript, slot: new AssistantProfileSlot(profile), memorySources: sources);

        transcript.Received(1).ArchiveAsync(AssistantSessionHost.AssistantPaneId, Arg.Any<CancellationToken>());
        var options = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(driver.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(ISessionDriver.StartAsync)).GetArguments()[6]);
        var brainNote = SessionStartDefaults.BrainNote(Brain, $"Bootstrap {Brain}.");
        var appended = options[WellKnownPluginSessionOptions.AppendSystemPrompt];
        Assert.Equal(brainNote, options[WellKnownPluginSessionOptions.BrainInstruction]);
        Assert.StartsWith(brainNote + "\n\n", appended, StringComparison.Ordinal);
        Assert.Equal(1, appended.Split(brainNote).Length - 1);
        // The consent paragraph follows the settings' own bypass, as it did before a brain was chosen.
        var standing = AssistantStandingInstruction.Compose(
            profile.SystemPrompt, replacesDefault: false, memory: null, consentCardAsks: !new AssistantSettings().ConsentBypassAll);
        Assert.Contains(standing, appended, StringComparison.Ordinal);
    }

    // A provider that vouches for compacting its own conversation — the one capability AC-664 turns on.
    private static SessionCapabilities _CompactingProvider() =>
        SessionCapabilities.ClaudeCli with { SupportsContextCompaction = true };

    // The hand-over runs fire-and-forget off a property change and tears the old runtime down on the way, which is a
    // real await — so the replacement lands a beat after the reading that triggered it, not inside it.
    private static SessionViewModel _ReplacementOf(AssistantSessionHost host, SessionViewModel previous)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (host.Session is null || ReferenceEquals(host.View(), previous))
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("The assistant was not handed over to a fresh session within 5s.");
            }

            Thread.Sleep(10);
        }

        return Assert.IsType<SessionViewModel>(host.View());
    }

    private static (AssistantSessionHost Host, SessionViewModel Session, ISessionDriver Driver) _StartedAssistantOn(
        SessionCapabilities capabilities, ISessionStateStore? sessionState = null, ISessionTranscriptStore? transcript = null,
        double contextCompactionPercent = 80, AssistantProfileSlot? slot = null, IProjectMemorySourceRegistry? memorySources = null)
    {
        // AC-1090: the transcript layer hangs off the pane now, not off the host — the host only asks the pane it
        // just made to replay it or roll it aside. Passed in here the way the container passes it in production.
        transcript ??= _EmptyTranscriptStore();
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyEvents());
        driver.Capabilities.Returns(capabilities);
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);

        var cockpit = Dispatcher.UIThread.Invoke(() => _CockpitWithSessionFactory(
            () => TestSessions.Pane(new SessionManager(factory), transcriptStore: transcript)));
        var host = Dispatcher.UIThread.Invoke(() => _Host(
            enabled: true, slot: slot ?? _ConfiguredSlot(), cockpit: cockpit, sessionState: sessionState,
            contextCompactionPercent: contextCompactionPercent, memorySources: memorySources));

        var session = Dispatcher.UIThread.Invoke(() => _Pane(host.EnsureStartedAsync().GetAwaiter().GetResult()));
        Assert.NotNull(session);
        return (host, session!, driver);
    }

    // The signal production reads: a turn completes and the provider's usage poll reports the fill.
    private static void _ReportAFullContext(SessionViewModel session, ISessionDriver driver, double fill)
    {
        driver.CurrentStatus.Returns(new SessionStatusFeed(fill, []));
        Dispatcher.UIThread.Invoke(() => session.Apply(new TurnCompleted
        {
            SessionId = "S1",
            Subtype = "success",
            Result = "done",
            IsError = false,
            Usage = new TokenUsage(1_000, 2_000, 0, 0),
            TotalCostUsd = 0.01,
        }));
    }

    // ── The restart (the operator's only way to reach a start-time setting) ───────────────────────────────────

    /// <summary>
    /// The whole difference between the two entry points. <see cref="AssistantSessionHost.EnsureStartedAsync"/>
    /// replaces an instance only once it is dead — which is right for a lazy start and useless for an operator
    /// who has just changed a setting that is read at a launch. bypassPermissions is choosable at a start and
    /// never live (<c>SessionOptionCatalog</c> splits <c>LivePermissionModes</c> off <c>AllPermissionModes</c> for
    /// exactly that reason), so "applies at the next start" needed a next start to exist.
    /// </summary>
    [Fact]
    public void EnsureStarted_WithAHealthyInstance_HandsItBack_WhileRestart_ReplacesIt()
    {
        var host = Dispatcher.UIThread.Invoke(() => _Host(enabled: true, slot: _ConfiguredSlot()));
        var running = Dispatcher.UIThread.Invoke(() => new _RunningSession());
        Dispatcher.UIThread.Invoke(() => host.Session = TestSessions.Assistant(running));

        var kept = Dispatcher.UIThread.Invoke(() => _Pane(host.EnsureStartedAsync().GetAwaiter().GetResult()));
        Assert.Same(running, kept);
        Assert.Same(running, host.View());

        Dispatcher.UIThread.Invoke(() => host.RestartAsync().GetAwaiter().GetResult());

        // Gone, and not quietly left in place looking reachable. (Nothing takes its place here: this cockpit has no
        // session factory, so the fresh start says so — which is the failure path, reported, rather than silence.)
        Assert.NotSame(running, host.View());
        Assert.Equal(AssistantActivity.Unavailable, host.Activity);
        Assert.NotNull(host.UnavailableReason);
    }

    /// <summary>
    /// AC-1089 criterion 4: startup compaction drops state for every pane its roster does not name (AC-410), and the
    /// assistant owns no workspace pane — so each start erased its saved conversation id before the resume above
    /// could read it, and no <c>--resume</c> was ever sent. Drives the real roster and the real store the way
    /// <c>CockpitBackend.ReconcileWorktreesAndCompactStateAsync</c> does: a substituted store is exactly what hid this,
    /// because the gap was between compaction and the read rather than inside either half.
    /// </summary>
    [Fact]
    public async Task TheAssistantsResume_SurvivesTheStartupCompaction_ThatDropsPanesTheRosterDoesNotName()
    {
        var path = Path.Combine(Path.GetTempPath(), $"session-state-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new SessionStateStore(path, NullLogger<SessionStateStore>.Instance);
            await store.RecordAsync(_StateFor("ai-1", "conv-elsewhere"));
            await store.RecordAsync(_StateFor(AssistantSessionHost.AssistantPaneId, "conv-assistant"));

            await store.CompactAsync(await SessionRestoreRoster.PaneIdsAsync(_WorkspacesNaming("ai-1")));

            var host = Dispatcher.UIThread.Invoke(() => _Host(enabled: true, slot: _ConfiguredSlot(), sessionState: store));

            // Awaited rather than the `Invoke(... GetResult())` the sibling tests use: those hand the host a
            // substitute whose read completes synchronously, while this one really touches the disk and would
            // post its `ConfigureAwait(true)` continuation to the very thread `Invoke` is blocking.
            var resume = await host._ResolveResumeAsync(default);

            Assert.Equal(SessionResumeMode.BySessionId, resume.Mode);
            Assert.Equal("conv-assistant", resume.SessionId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IWorkspaceSettingsStore _WorkspacesNaming(string paneId)
    {
        var workspace = Workspace.Create("Work", WorkspaceType.Sessions).WithPane(new WorkspacePane(paneId, PaneKind.AiSession));
        var store = Substitute.For<IWorkspaceSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new WorkspaceSettings { Workspaces = [workspace], ActiveWorkspaceId = workspace.Id });
        return store;
    }

    /// <summary>
    /// AC-1089 criterion 3: a state file that fails to read is not "nothing was ever saved" — the two used to look
    /// identical (<c>LoadAsync</c> collapses them), which threw a real conversation away on a transient read error
    /// with nothing said about it. <c>TryLoadAsync</c> tells them apart; this asserts the failure path still ends
    /// in a fresh conversation (there is no id to resume either way) but is reached, not skipped.
    /// </summary>
    [Fact]
    public void TheAssistantsResume_WhenTheStateCouldNotBeRead_IsAFreshConversation_NotSilently()
    {
        var state = Substitute.For<ISessionStateStore>();
        state.TryLoadAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<SessionStateRecord>?)null);
        var host = Dispatcher.UIThread.Invoke(() => _Host(enabled: true, slot: _ConfiguredSlot(), sessionState: state));

        var resume = Dispatcher.UIThread.Invoke(() => host._ResolveResumeAsync(default).GetAwaiter().GetResult());

        Assert.Equal(SessionResumeMode.New, resume.Mode);
    }

    /// <summary>
    /// A session that goes away with a consent card still open is what hangs its caller forever — the broker has
    /// no timeout of its own. Answered No, because the operator restarted rather than clicking Allow.
    /// </summary>
    [Fact]
    public void Restarting_OverAnUnansweredConsentCard_AnswersIt_RatherThanLeavingItsCallerWaiting()
    {
        var broker = Substitute.For<IConsentPrompts>();
        var prompt = new ConsentQuestion(
            Guid.NewGuid(),
            new ConsentRequest(
                "Run a command",
                "rm -rf /",
                new ConsentSource(AssistantSessionHost.AssistantPaneId, null, "Terminal MCP"),
                "scope",
                ConsentRisk.Dangerous),
            CanRemember: false);

        var host = Dispatcher.UIThread.Invoke(() => _Host(enabled: true, slot: _ConfiguredSlot()));
        Dispatcher.UIThread.Invoke(() => host.Session = TestSessions.Assistant(new _RunningSession
        {
            PendingConsent = new ConsentPromptViewModel(prompt, broker),
        }));

        Dispatcher.UIThread.Invoke(() => host.RestartAsync().GetAwaiter().GetResult());

        broker.Received(1).Respond(prompt.Id, ConsentOutcome.Denied, false);
    }

    // ── Clearing the conversation (AC-1261) ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Criterion 7 (V2): a clear queued by the tool must not run while the assistant is mid-turn — that would tear
    /// down the very session the tool call is running in and drop the result it is waiting to return. It runs the
    /// moment the turn ends, through the same gate (<see cref="AssistantSessionHost.ShouldHandOver"/>) AC-596's
    /// hand-over uses.
    /// </summary>
    [Fact]
    public void RequestingAClear_WhileBusy_WaitsForTheTurnToEnd_ThenRunsExactlyOnce()
    {
        var transcript = Substitute.For<ISessionTranscriptStore>();
        var (host, first, _) = _StartedAssistantOn(SessionCapabilities.ClaudeCli, transcript: transcript);

        // The start above archives too (no saved conversation to resume) — cleared so the count below is the
        // clear's own, not that unrelated archive from getting the assistant running in the first place.
        transcript.ClearReceivedCalls();

        Dispatcher.UIThread.Invoke(() => first.IsBusy = true);

        Assert.True(Dispatcher.UIThread.Invoke(host.RequestConversationClear));

        // Not run while busy: give the (fire-and-forget, if wrongly triggered) clear a moment, then assert nothing
        // moved.
        Thread.Sleep(200);
        Assert.Same(first, host.View());

        // The turn ends: the same property change AC-596's hand-over watches is what runs the queued clear.
        Dispatcher.UIThread.Invoke(() => first.IsBusy = false);

        var second = _ReplacementOf(host, first);
        var divider = Assert.Single(second.Transcript, entry => entry.IsDivider);
        Assert.Equal("The earlier conversation was cleared — this conversation starts here", divider.Text);

        // Exactly once: `_StartAsync` is the only place that archives, and a clear must not add a second archive path.
        transcript.Received(1).ArchiveAsync(AssistantSessionHost.AssistantPaneId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A second call within the same turn must not queue a second clear — it finds one already queued and says so,
    /// which is what lets the tool report the difference to the assistant.
    /// </summary>
    [Fact]
    public void RequestingAClear_ASecondTimeInTheSameTurn_ReportsItIsAlreadyQueued()
    {
        var host = Dispatcher.UIThread.Invoke(() => _Host(enabled: true, slot: _ConfiguredSlot()));
        Dispatcher.UIThread.Invoke(() => host.Session = TestSessions.Assistant(new _RunningSession { IsBusy = true }));

        Assert.True(Dispatcher.UIThread.Invoke(host.RequestConversationClear));
        Assert.False(Dispatcher.UIThread.Invoke(host.RequestConversationClear));
    }

    /// <summary>
    /// Criterion 8: what the operator asked to be remembered and the assistant's own current-state note are read
    /// fresh off disk by every start, <see cref="AssistantSessionHost.ClearConversationAsync"/> included — this
    /// proves a clear neither writes to nor loses either file, and that the fresh launch it produces still carries
    /// both, each under its own heading.
    /// </summary>
    [Fact]
    public async Task ClearingTheConversation_LeavesMemoryAndTheCurrentStateNoteUntouched_AndCarriesBothIntoTheFreshLaunch()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var memoryPath = Path.Combine(directory.FullName, "assistant-memory.md");
            var statePath = Path.Combine(directory.FullName, "assistant-state.md");
            var memory = new AssistantMemoryFile(memoryPath, statePath);
            await memory.RememberAsync("The operator is called Raymond.", AssistantMemoryScope.Behaviour);
            await memory.NoteCurrentStateAsync("We are on AC-1261; the clear tool is being built.");
            var memoryBefore = await File.ReadAllBytesAsync(memoryPath);
            var stateBefore = await File.ReadAllBytesAsync(statePath);

            var driver = Substitute.For<ISessionDriver>();
            driver.Events.Returns(_EmptyEvents());
            var factory = Substitute.For<ISessionDriverFactory>();
            factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
            var cockpit = Dispatcher.UIThread.Invoke(() => _CockpitWithSessionFactory(
                () => TestSessions.Pane(new SessionManager(factory))));
            var host = Dispatcher.UIThread.Invoke(() => _Host(
                enabled: true, slot: _ConfiguredSlot(), cockpit: cockpit, memory: memory));

            // InvokeAsync, not the `Invoke(... GetResult())` the sibling tests use: this is the only test whose
            // memory is a real `AssistantMemoryFile`, so the start path does real file I/O — and blocking the UI
            // thread on a chain that posts its continuations back to it deadlocks when that read runs async.
            var first = await Dispatcher.UIThread.InvokeAsync(() => host.EnsureStartedAsync());
            Assert.NotNull(first);

            // Awaiting the clear itself is also what makes the assertions below safe: the fresh launch is complete
            // when this returns, so nothing is left running past the end of the test.
            await Dispatcher.UIThread.InvokeAsync(() => host.ClearConversationAsync());

            Assert.Equal(memoryBefore, await File.ReadAllBytesAsync(memoryPath));
            Assert.Equal(stateBefore, await File.ReadAllBytesAsync(statePath));

            // Twice: the launch before the clear and the one it produced both carried the memory and the note —
            // proving the clear did not lose either on the way through.
            _ = driver.Received(2).StartAsync(
                Arg.Any<SessionProfile?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlySet<string>?>(),
                Arg.Any<string?>(), Arg.Any<SessionResume?>(),
                Arg.Is<IReadOnlyDictionary<string, string>?>(options =>
                    options != null
                    && options[WellKnownPluginSessionOptions.AppendSystemPrompt].Contains(
                        AssistantStandingInstruction.MemoryHeading, StringComparison.Ordinal)
                    && options[WellKnownPluginSessionOptions.AppendSystemPrompt].Contains(
                        AssistantStandingInstruction.CurrentStateHeading, StringComparison.Ordinal)
                    && options[WellKnownPluginSessionOptions.AppendSystemPrompt].Contains("Raymond", StringComparison.Ordinal)
                    && options[WellKnownPluginSessionOptions.AppendSystemPrompt].Contains("AC-1261", StringComparison.Ordinal)),
                Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    /// <summary>A session whose runtime is up, which is the state that cannot be produced without a real child process.</summary>
    private static SessionViewModel? _Pane(IAssistantSession? session) => (session as SessionPanelHandle)?.Pane as SessionViewModel;

    private sealed class _RunningSession : SessionViewModel
    {
        public override bool IsSessionReady => true;
    }

    private static SessionProfile _Profile() => new("assistant-local", new ClaudeConfig("/tmp/claude"));

    private static SessionProfile _ProfileWithDefaults(params (string Key, string Value)[] optionDefaults) =>
        _Profile() with
        {
            // The legacy typed trio is left blank deliberately: a plugin profile stores nothing there, and a test
            // that filled them in would be asserting against the migration path rather than the live one.
            Defaults = new ProfileDefaults(string.Empty, string.Empty, string.Empty)
            {
                OptionDefaults = optionDefaults.ToDictionary(option => option.Key, option => option.Value, StringComparer.OrdinalIgnoreCase),
            },
        };

    private static AssistantSessionHost _Host(
        bool enabled,
        AssistantProfileSlot slot,
        IMcpServerCatalog? catalog = null,
        Cockpit.Core.Sessions.ReadingLevel readingLevel = Cockpit.Core.Sessions.ReadingLevel.Developer,
        IAssistantProfileStore? profiles = null,
        ISessionStateStore? sessionState = null,
        bool speakReplies = true,
        CockpitViewModel? cockpit = null,
        IAssistantMemory? memory = null,
        NodeControllerPresence? presence = null,
        double contextCompactionPercent = 80,
        IProjectMemorySourceRegistry? memorySources = null)
    {
        var settings = Substitute.For<IAssistantSettingsStore>();
        settings.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new AssistantSettings
            {
                IsEnabled = enabled,
                ReadingLevel = readingLevel,
                SpeakReplies = speakReplies,
                ContextCompactionPercent = contextCompactionPercent,
            });

        if (profiles is null)
        {
            profiles = Substitute.For<IAssistantProfileStore>();
            profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(slot);
        }

        if (sessionState is null)
        {
            sessionState = Substitute.For<ISessionStateStore>();
            sessionState.LoadAsync(Arg.Any<CancellationToken>()).Returns([]);
            sessionState.TryLoadAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<SessionStateRecord>?>([]);
        }

        var sessionStateRecorder = new SessionStateRecorder(
            sessionState, new SessionConversationTracker(), NullLogger<SessionStateRecorder>.Instance);
        var desktop = cockpit ?? new CockpitViewModel();

        return new AssistantSessionHost(
            new DesktopSessionSeams(() => desktop), presence ?? new NodeControllerPresence(), settings, profiles,
            sessionState, sessionStateRecorder,
            catalog ?? _Catalog(), memory ?? Substitute.For<IAssistantMemory>(),
            NullLogger<AssistantSessionHost>.Instance, memorySources: memorySources);
    }

    private static ISessionTranscriptStore _EmptyTranscriptStore()
    {
        var store = Substitute.For<ISessionTranscriptStore>();
        store.TryLoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<IReadOnlyList<TranscriptSnapshotEntry>?>([]);
        return store;
    }

    // A recorded row. The id is what AC-1090's log keys a row's versions on; these tests only need it unique.
    private static TranscriptSnapshotEntry _Recorded(string kind, string text, string? inputJson = null) =>
        new(Guid.NewGuid().ToString("n"), kind, text, null, inputJson, null, null, false, DateTimeOffset.Now);

    // A cockpit whose sessions actually start (against a fake driver) rather than the no-factory default the rest
    // of this file uses — needed only by the hand-over test, which has to observe a second, real SessionViewModel.
    private static CockpitViewModel _CockpitWithSessionFactory(Func<SessionViewModel> sessionFactory)
    {
        var notifications = Substitute.For<Cockpit.Core.Abstractions.Notifications.INotificationSettingsStore>();
        notifications.LoadAsync().Returns(new Cockpit.Core.Notifications.NotificationSettings());
        var transcriptDisplay = Substitute.For<Cockpit.Core.Abstractions.TranscriptDisplay.ITranscriptDisplaySettingsStore>();
        transcriptDisplay.LoadAsync().Returns(new Cockpit.Core.TranscriptDisplay.TranscriptDisplaySettings());
        var sessionBehavior = Substitute.For<Cockpit.Core.Abstractions.SessionBehavior.ISessionBehaviorSettingsStore>();
        sessionBehavior.LoadAsync().Returns(new Cockpit.Core.SessionBehavior.SessionBehaviorSettings());
        var layout = Substitute.For<Cockpit.Core.Abstractions.Layout.ILayoutSettingsStore>();
        layout.LoadAsync().Returns(new Cockpit.Core.Layout.LayoutSettings());
        var voice = Substitute.For<Cockpit.Core.Abstractions.Voice.IVoiceSettingsStore>();
        voice.LoadAsync().Returns(new Cockpit.Core.Voice.VoiceSettings());
        var terminal = Substitute.For<Cockpit.Core.Abstractions.Terminal.ITerminalSettingsStore>();
        terminal.LoadAsync().Returns(new Cockpit.Core.Terminal.TerminalSettings());

        return new CockpitViewModel(
            sessionFactory,
            () => new TtyViewModel(),
            Substitute.For<ISessionDialogService>(),
            Substitute.For<Cockpit.Core.Abstractions.Audio.IAudioCaptureService>(),
            Substitute.For<Cockpit.Core.Abstractions.Audio.IAudioPlaybackService>(),
            Substitute.For<Cockpit.Core.Abstractions.Notifications.IAttentionNotifier>(),
            notifications,
            transcriptDisplay,
            sessionBehavior,
            layout,
            voice,
            terminal);
    }

    private static async IAsyncEnumerable<SessionEvent> _EmptyEvents(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Open until the runtime cancels it: a live driver's stream ends only when its process does (AC-693).
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }

    private static SessionStateRecord _StateFor(string paneId, string conversationId) =>
        new(paneId, "profile", null, conversationId, SessionConversationIdState.Known, null, null, null, null, DateTimeOffset.UtcNow);

    private static IMcpServerCatalog _Catalog(params McpServerConfig[] servers)
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<McpServerConfig>>(_ => servers);
        return catalog;
    }

    private static AssistantProfileSlot _ConfiguredSlot() => new(_Profile());
}
