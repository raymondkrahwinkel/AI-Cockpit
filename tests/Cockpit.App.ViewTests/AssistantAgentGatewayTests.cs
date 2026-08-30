using System.Reflection;
using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Assistant;
using Cockpit.Core.Layout;
using Cockpit.Core.Worktrees;
using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The host-side spawn service (AC-545): <see cref="AssistantAgentGateway"/> over a real
/// <see cref="CockpitViewModel"/>, which is the only thing that can answer whether a spawn actually lands on the
/// desk it was told to and leaves the operator where they were.
/// </summary>
/// <remarks>
/// <b>Why this file lives in the view tests.</b> It used to sit in <c>Cockpit.Core.Tests</c>, which stands up no
/// Avalonia application and therefore has no dispatcher anyone pumps. Every call here goes through the gateway's
/// UI-thread marshalling, and with no dispatcher that step has two outcomes, decided by nothing more than which
/// thread happened to touch <c>Dispatcher.UIThread</c> first: on that thread the marshalling is skipped inline and
/// the test proves nothing about it, on any other it queues onto a loop nobody runs and the whole test host hangs.
/// A test that is green for the first reason is worse than no test, and the second cost an eighteen-minute CI job
/// that never printed a summary. Here the collection fixture owns a real UI thread with a running main loop, so the
/// marshalling is the real thing: set-up runs on that thread, and the gateway is awaited from the test thread —
/// which is the branch production actually takes.
/// <para>
/// <b>What these tests are for.</b> Not the scoping rule — that is <see cref="SpawnTarget"/>'s two doors and is
/// decided before this class is reached. What is pinned here is the duller half the gateway does own: that a
/// request it cannot carry out comes back as a <em>reason</em> rather than an exception, that every one of those
/// refusals reaches the trail (a gate that only logs what it let through cannot show it working), and that a
/// refusal leaves nothing behind — the next call is served normally.
/// </para>
/// <para>
/// <b>Why the audit log is a real recorder and not a substitute.</b> Every assertion here about a refusal is an
/// assertion about what was <em>written</em>, so the fake keeps the entries rather than a call count.
/// </para>
/// </remarks>
[Collection("avalonia")]
public class AssistantAgentGatewayTests
{
    private const string ProfileLabel = "work";

    [Fact]
    public async Task SpawnOntoAWorkspaceIdThatNamesNothing_IsRefusedWithAReason_AndTheRefusalReachesTheTrail()
    {
        var (gateway, _, trail) = Dispatcher.UIThread.Invoke(
            () => _Gateway(_Settings(_Desk("Sessions", WorkspaceType.Sessions))));

        var result = await gateway.SpawnAsync(_Request("no-such-desk"));

        Assert.False(result.Ok);
        Assert.Null(result.PaneId);
        Assert.Contains("no workspace with id 'no-such-desk'", result.Error);

        var entry = Assert.Single(trail.Entries);
        Assert.Equal(AssistantSpawnAction.Start, entry.Action);
        Assert.Equal(SpawnCaller.Assistant, entry.Caller);
        Assert.Equal("no-such-desk", entry.WorkspaceId);
        Assert.Null(entry.PaneId);
        Assert.Equal(result.Error, entry.Refusal);
    }

    [Fact]
    public async Task ListProfiles_NamesAPluginProfilesOwnProvider_AndWhatItIsConfiguredToRunAt()
    {
        // AC-647. Both halves were unreadable before: every plugin-backed profile reported the provider as the bare
        // enum value "Plugin" — Claude and Codex indistinguishable — and what it ran at lived only in cockpit.json,
        // which the in-app assistant cannot open at all.
        var registry = Substitute.For<IPluginProviderRegistry>();
        registry.Resolve("claude").Returns(_ProviderRegistration("claude", "Claude", new PluginSessionOptionDescriptor(
            "effort", "Effort", [new PluginSessionOptionValue("high", "High")], "medium")));

        var gateway = Dispatcher.UIThread.Invoke(() => _GatewayOverProfiles(
            registry,
            new SessionProfile("work", new PluginProviderConfig("claude", "{}"))
            {
                Defaults = new ProfileDefaults(string.Empty, string.Empty, string.Empty, AutoApproveTools: false)
                {
                    OptionDefaults = new Dictionary<string, string> { ["effort"] = "high" },
                },
            },
            new SessionProfile("local", new LmStudioConfig("http://localhost:1234", "qwen2.5"))));

        var profiles = await gateway.ListProfilesAsync();

        var claude = profiles.Single(profile => profile.Label == "work");
        Assert.Equal("Claude", claude.Provider);
        var effort = Assert.Single(claude.Options);
        Assert.Equal(("effort", "Effort", "high", "High", true),
            (effort.Key, effort.Label, effort.Value, effort.ValueLabel, effort.SetOnProfile));

        // And the provider with no declared options keeps its own shape: a model, and nothing borrowed from Claude.
        var local = profiles.Single(profile => profile.Label == "local");
        Assert.Equal("LmStudio", local.Provider);
        Assert.Equal("qwen2.5", local.Model);
        Assert.Empty(local.Options);
    }

    /// <summary>
    /// Every workspace type there is, taken from <see cref="WorkspaceType"/> itself rather than from a list
    /// written out here — the type is deliberately an open set (a plugin registers its own), so a hand-written
    /// list would pin the two the author happened to think of and stay green when a third arrived.
    /// </summary>
    public static TheoryData<string> EveryWorkspaceTypeThatCannotHostASession()
    {
        var data = new TheoryData<string>();
        foreach (var type in _AllKnownWorkspaceTypes().Where(type => type != WorkspaceType.Sessions))
        {
            data.Add(type.Id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryWorkspaceTypeThatCannotHostASession))]
    public async Task SpawnOntoADeskThatIsNotASessionsDesk_IsRefused_AndTheRefusalReachesTheTrail(string workspaceTypeId)
    {
        // A dashboard (or a projects overview, or a plugin's own desk) would take the session and never draw it:
        // the pane would run, cost money and be unreachable. Refusing is the kinder half of that pair.
        var desk = _Desk("Monitoring", WorkspaceType.FromId(workspaceTypeId));
        var (gateway, _, trail) = Dispatcher.UIThread.Invoke(() => _Gateway(_Settings(desk)));

        var result = await gateway.SpawnAsync(_Request(desk.Id));

        Assert.False(result.Ok);
        Assert.Null(result.PaneId);
        Assert.Contains("cannot show a session", result.Error);

        var entry = Assert.Single(trail.Entries);
        Assert.Equal(desk.Id, entry.WorkspaceId);
        Assert.Equal("Monitoring", entry.WorkspaceName);
        Assert.Equal(result.Error, entry.Refusal);
    }

    [Fact]
    public async Task ASessionsDeskIsTheOneTypeThatIsNotRefusedForItsType()
    {
        // The other side of the theory above: the refusal is about the type, not about every desk. Without this
        // the theory would still pass if the gateway refused everything.
        var desk = _Desk("Release", WorkspaceType.Sessions);
        var (gateway, _, _) = Dispatcher.UIThread.Invoke(() => _Gateway(_Settings(desk)));

        var result = await gateway.SpawnAsync(_Request(desk.Id));

        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public async Task SpawnThatTriesToSetThePermissionMode_IsRefused_AndNothingIsStarted()
    {
        // AC-648 criterion 3 (Raymond, 2026-08-08), asserted on the whole spawn path rather than on the merge alone:
        // the claim is that the request is turned away, not that the mode happened to survive it. A spawn that
        // stripped the key and started anyway would leave the assistant reporting a session it never got.
        var desk = _Desk("Release", WorkspaceType.Sessions);
        var (gateway, cockpit, trail) = Dispatcher.UIThread.Invoke(
            () => _Gateway(_Settings(desk), host: null, _PluginProfile(("permission-mode", "default")), _ClaudeRegistry()));
        var before = Dispatcher.UIThread.Invoke(() => cockpit.Sessions.Count);

        var result = await gateway.SpawnAsync(_Request(desk.Id) with
        {
            OptionOverrides = new Dictionary<string, string> { ["permission-mode"] = "bypassPermissions" },
        });

        Assert.False(result.Ok);
        Assert.Null(result.PaneId);
        Assert.Contains("not something a spawn may set", result.Error);
        Assert.Equal(before, Dispatcher.UIThread.Invoke(() => cockpit.Sessions.Count));
        Assert.Equal(result.Error, Assert.Single(trail.Entries).Refusal);
    }

    [Fact]
    public async Task SpawnThatOverridesAnOptionTheProviderDoesDeclare_IsNotRefused()
    {
        // The other side of the two refusals below: `effort` is Claude's own key, so it goes through. What the
        // merged map then contains — criterion 4, every unnamed key still the profile's own — is pinned where the
        // merging happens, in `SpawnOptionOverridesTests`; a started session keeps its launch options private.
        var desk = _Desk("Release", WorkspaceType.Sessions);
        var (gateway, _, _) = Dispatcher.UIThread.Invoke(() => _Gateway(
            _Settings(desk), host: null, _PluginProfile(("permission-mode", "bypassPermissions"), ("effort", "high")),
            _ClaudeRegistry()));

        var result = await gateway.SpawnAsync(_Request(desk.Id) with
        {
            OptionOverrides = new Dictionary<string, string> { ["effort"] = "low" },
        });

        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public async Task SpawnWithAnOptionKeyTheProviderNeverDeclared_IsRefusedWithWhatItDoesTake()
    {
        // Criterion 2. The refusal is the whole point of AC-649 landing first: without a declared schema this would
        // be accepted and handed to a CLI that has no such flag, and the session would come up wrong.
        var desk = _Desk("Release", WorkspaceType.Sessions);
        var (gateway, _, _) = Dispatcher.UIThread.Invoke(
            () => _Gateway(_Settings(desk), host: null, _PluginProfile(), _ClaudeRegistry()));

        var result = await gateway.SpawnAsync(_Request(desk.Id) with
        {
            OptionOverrides = new Dictionary<string, string> { ["sandbox-policy"] = "read-only" },
        });

        Assert.False(result.Ok);
        Assert.Contains("no option called 'sandbox-policy'", result.Error);
        Assert.Contains("'effort'", result.Error);
    }

    [Fact]
    public async Task SpawnWithAProfileLabelTheCockpitDoesNotHave_IsRefused_NamesTheOnesItDoes_AndReachesTheTrail()
    {
        // By label and never by "the first one that looks close": the profile decides provider and model, so a
        // near-miss is a bill the operator did not agree to.
        var desk = _Desk("Release", WorkspaceType.Sessions);
        var (gateway, _, trail) = Dispatcher.UIThread.Invoke(() => _Gateway(_Settings(desk)));

        var result = await gateway.SpawnAsync(_Request(desk.Id) with { ProfileLabel = "opus-everything" });

        Assert.False(result.Ok);
        Assert.Contains("no profile called 'opus-everything'", result.Error);
        Assert.Contains($"'{ProfileLabel}'", result.Error);

        var entry = Assert.Single(trail.Entries);
        Assert.Equal("opus-everything", entry.Profile);
        Assert.Equal(desk.Id, entry.WorkspaceId);
        Assert.Equal(result.Error, entry.Refusal);
    }

    [Fact]
    public async Task WhenTheCockpitCannotStartASessionAtAll_ThatIsAReasonToo_NotAnException()
    {
        // A host with no session machinery behind it — the launch declines and hands back nothing. The point is
        // that the assistant is told, in a sentence it can say out loud, rather than the tool call blowing up as
        // "the tool failed", which is the one answer that helps nobody.
        var desk = _Desk("Release", WorkspaceType.Sessions);
        var (gateway, _, trail) = Dispatcher.UIThread.Invoke(
            () => _Gateway(_Settings(desk), _CockpitWithoutSessionMachinery()));

        var result = await gateway.SpawnAsync(_Request(desk.Id));

        Assert.False(result.Ok);
        Assert.Null(result.PaneId);
        Assert.Contains("could not start a session", result.Error);

        var entry = Assert.Single(trail.Entries);
        Assert.Equal(desk.Id, entry.WorkspaceId);
        Assert.Equal(result.Error, entry.Refusal);
    }

    [Fact]
    public async Task StoppingTheAssistantsOwnPane_IsRefusedByIdentity_EvenWhenThatPaneIsPerfectlyFindable()
    {
        // Asserted by identity rather than by "it happens not to be in Sessions": the assistant sits outside both
        // session collections today, so a check that leaned on that would pass for a reason that is a placement
        // and not a rule. Here the pane is deliberately findable and an ordinary agent session in every other
        // respect — the only thing wrong with it is who it is.
        var desk = _Desk("Release", WorkspaceType.Sessions);
        var (gateway, cockpit, trail) = Dispatcher.UIThread.Invoke(() => _Gateway(_Settings(desk)));
        Dispatcher.UIThread.Invoke(() =>
        {
            var impostor = new SessionViewModel { WorkspaceId = desk.Id, Title = "The assistant" };
            impostor.AdoptPaneId(AssistantIdentity.PaneId);
            cockpit.Sessions.Add(impostor);
            Assert.NotNull(cockpit.FindSession(AssistantIdentity.PaneId));
        });

        var result = await gateway.StopAsync(AssistantIdentity.PaneId);

        Assert.False(result.Ok);
        Assert.Contains("my own session", result.Error);
        Dispatcher.UIThread.Invoke(
            () => Assert.Contains(cockpit.Sessions, session => session.PaneId == AssistantIdentity.PaneId));

        var entry = Assert.Single(trail.Entries);
        Assert.Equal(AssistantSpawnAction.Stop, entry.Action);
        Assert.Equal(AssistantIdentity.PaneId, entry.PaneId);
        Assert.Equal(result.Error, entry.Refusal);
    }

    [Fact]
    public async Task StoppingAPaneIdNothingIsRunningUnder_IsRefused_AndTheRefusalReachesTheTrail()
    {
        var (gateway, _, trail) = Dispatcher.UIThread.Invoke(
            () => _Gateway(_Settings(_Desk("Release", WorkspaceType.Sessions))));

        var result = await gateway.StopAsync("pane-that-closed");

        Assert.False(result.Ok);
        Assert.Contains("no session with pane id 'pane-that-closed'", result.Error);

        var entry = Assert.Single(trail.Entries);
        Assert.Equal(AssistantSpawnAction.Stop, entry.Action);
        Assert.Equal("pane-that-closed", entry.PaneId);
        Assert.Equal(result.Error, entry.Refusal);
    }

    [Fact]
    public async Task StoppingAPlainTerminalPane_IsRefused_AndTheRefusalReachesTheTrail()
    {
        // A terminal has a pane id and no agent on the other end — the same test the read path lists by, so what
        // can be stopped is exactly what could be seen.
        var desk = _Desk("Release", WorkspaceType.Sessions);
        var (gateway, cockpit, trail) = Dispatcher.UIThread.Invoke(() => _Gateway(_Settings(desk)));
        var terminal = Dispatcher.UIThread.Invoke(() =>
        {
            var tty = new TtyViewModel { WorkspaceId = desk.Id, Title = "pwsh", ShowPluginHeaderItems = false };
            cockpit.Sessions.Add(tty);
            return tty;
        });

        var result = await gateway.StopAsync(terminal.PaneId);

        Assert.False(result.Ok);
        Assert.Contains("is a terminal pane", result.Error);
        Dispatcher.UIThread.Invoke(
            () => Assert.Contains(cockpit.Sessions, session => session.PaneId == terminal.PaneId));

        var entry = Assert.Single(trail.Entries);
        Assert.Equal(AssistantSpawnAction.Stop, entry.Action);
        Assert.Equal(terminal.PaneId, entry.PaneId);
        Assert.Equal(result.Error, entry.Refusal);
    }

    // ── worktree_handover (AC-719 ronde B) — refusals are hard, not best-effort ────────────────────────────────

    [Fact]
    public async Task Handover_PathNotAManagedWorktree_IsRefused_AndTheRefusalReachesTheTrail()
    {
        var worktrees = Substitute.For<IWorktreeManager>();
        worktrees.ListAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorktreeRecord>());
        var (gateway, _, trail) = Dispatcher.UIThread.Invoke(
            () => _Gateway(_Settings(_Desk("Release", WorkspaceType.Sessions)), worktreeManager: worktrees));

        var result = await gateway.HandoverWorktreeAsync("/wt/nope", "pane-target");

        Assert.False(result.Ok);
        Assert.Contains("No managed worktree", result.Error);
        var entry = Assert.Single(trail.Entries);
        Assert.Equal(AssistantSpawnAction.Handover, entry.Action);
        Assert.Equal(result.Error, entry.Refusal);
        await worktrees.DidNotReceiveWithAnyArgs().ReattachAsync(default!, default!);
    }

    [Fact]
    public async Task Handover_WorktreeOwnedByADifferentSession_IsRefused()
    {
        // Never a confused deputy: only the assistant's own worktrees may be given away, whoever asks.
        var worktrees = Substitute.For<IWorktreeManager>();
        var record = new WorktreeRecord("someone-elses-pane", "/repo", "/wt/theirs", "cockpit/x", "abc", DateTimeOffset.UtcNow);
        worktrees.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<WorktreeRecord> { record });
        var (gateway, _, trail) = Dispatcher.UIThread.Invoke(
            () => _Gateway(_Settings(_Desk("Release", WorkspaceType.Sessions)), worktreeManager: worktrees));

        var result = await gateway.HandoverWorktreeAsync("/wt/theirs", "pane-target");

        Assert.False(result.Ok);
        Assert.Contains("not mine to hand over", result.Error);
        Assert.Single(trail.Entries);
        await worktrees.DidNotReceiveWithAnyArgs().ReattachAsync(default!, default!);
    }

    [Fact]
    public async Task Handover_TargetPaneDoesNotExist_IsRefused()
    {
        var worktrees = Substitute.For<IWorktreeManager>();
        var record = new WorktreeRecord(AssistantIdentity.PaneId, "/repo", "/wt/mine", "cockpit/x", "abc", DateTimeOffset.UtcNow);
        worktrees.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<WorktreeRecord> { record });
        var (gateway, _, trail) = Dispatcher.UIThread.Invoke(
            () => _Gateway(_Settings(_Desk("Release", WorkspaceType.Sessions)), worktreeManager: worktrees));

        var result = await gateway.HandoverWorktreeAsync("/wt/mine", "pane-that-closed");

        Assert.False(result.Ok);
        Assert.Contains("no session with pane id 'pane-that-closed'", result.Error);
        Assert.Single(trail.Entries);
        await worktrees.DidNotReceiveWithAnyArgs().ReattachAsync(default!, default!);
    }

    [Fact]
    public async Task Handover_TargetIsAPlainTerminalPane_IsRefused()
    {
        var desk = _Desk("Release", WorkspaceType.Sessions);
        var worktrees = Substitute.For<IWorktreeManager>();
        var record = new WorktreeRecord(AssistantIdentity.PaneId, "/repo", "/wt/mine", "cockpit/x", "abc", DateTimeOffset.UtcNow);
        worktrees.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<WorktreeRecord> { record });
        var (gateway, cockpit, trail) = Dispatcher.UIThread.Invoke(
            () => _Gateway(_Settings(desk), worktreeManager: worktrees));
        var terminal = Dispatcher.UIThread.Invoke(() =>
        {
            var tty = new TtyViewModel { WorkspaceId = desk.Id, Title = "pwsh", ShowPluginHeaderItems = false };
            cockpit.Sessions.Add(tty);
            return tty;
        });

        var result = await gateway.HandoverWorktreeAsync("/wt/mine", terminal.PaneId);

        Assert.False(result.Ok);
        Assert.Contains("is a terminal pane", result.Error);
        Assert.Single(trail.Entries);
        await worktrees.DidNotReceiveWithAnyArgs().ReattachAsync(default!, default!);
    }

    [Fact]
    public async Task Handover_TargetIsTheAssistantsOwnPane_IsRefused()
    {
        var worktrees = Substitute.For<IWorktreeManager>();
        var (gateway, _, trail) = Dispatcher.UIThread.Invoke(
            () => _Gateway(_Settings(_Desk("Release", WorkspaceType.Sessions)), worktreeManager: worktrees));

        var result = await gateway.HandoverWorktreeAsync("/wt/mine", AssistantIdentity.PaneId);

        Assert.False(result.Ok);
        Assert.Contains("cannot be handed to it", result.Error);
        Assert.Single(trail.Entries);
        await worktrees.DidNotReceiveWithAnyArgs().ListAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handover_Succeeds_ReattachesTheWorktreeAndRecordsTheTrail()
    {
        var desk = _Desk("Release", WorkspaceType.Sessions);
        var worktrees = Substitute.For<IWorktreeManager>();
        var record = new WorktreeRecord(AssistantIdentity.PaneId, "/repo", "/wt/mine", "cockpit/x", "abc", DateTimeOffset.UtcNow);
        worktrees.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<WorktreeRecord> { record });
        var (gateway, cockpit, trail) = Dispatcher.UIThread.Invoke(
            () => _Gateway(_Settings(desk), worktreeManager: worktrees));
        var target = Dispatcher.UIThread.Invoke(() =>
        {
            var session = new SessionViewModel { WorkspaceId = desk.Id, Title = "AC-719 tests" };
            cockpit.Sessions.Add(session);
            return session;
        });
        worktrees.TransferAsync("/wt/mine", AssistantIdentity.PaneId, target.PaneId, Arg.Any<CancellationToken>())
            .Returns(record with { SessionId = target.PaneId });

        var result = await gateway.HandoverWorktreeAsync("/wt/mine", target.PaneId);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("/wt/mine", result.Path);
        Assert.Equal("cockpit/x", result.Branch);
        await worktrees.Received(1).TransferAsync("/wt/mine", AssistantIdentity.PaneId, target.PaneId, Arg.Any<CancellationToken>());
        Assert.Equal("cockpit/x", Dispatcher.UIThread.Invoke(() => target.WorktreeBranch));

        var entry = Assert.Single(trail.Entries);
        Assert.Equal(AssistantSpawnAction.Handover, entry.Action);
        Assert.Equal(target.PaneId, entry.PaneId);
        Assert.Null(entry.Refusal);
    }

    // ── AC-587: open_url's own door onto ExternalLink ──────────────────────────────────────────────────────────

    /// <summary>
    /// Criterion 4: a non-web scheme is refused through <c>ExternalLink.TryParseWebAddress</c> — the same guard
    /// every other web address in <c>Cockpit.App</c> is held to — and never through
    /// <c>ExternalLink.TryOpenWithSystemApp</c>, which opens a filesystem path with a program rather than a page.
    /// A value this refuses starts nothing, so this stays safe to run in CI unlike the accepted case (which would
    /// start a real browser on the machine running the test — the same reason <c>ExternalLinkTests</c> never
    /// exercises that branch either).
    /// </summary>
    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    public async Task OpenUrl_ANonWebScheme_IsRefused_AndStartsNothing(string url)
    {
        var (gateway, _, _) = Dispatcher.UIThread.Invoke(
            () => _Gateway(_Settings(_Desk("Release", WorkspaceType.Sessions))));

        var result = await gateway.OpenUrlAsync(url);

        Assert.False(result.Ok);
        Assert.Null(result.Url);
        Assert.Contains("not an absolute http(s) address", result.Error);
    }

    [Fact]
    public async Task ASpawnOntoANamedDesk_LandsThere_AndLeavesTheOperatorOnTheDeskTheyWereLookingAt()
    {
        // The whole reason the assistant is asked to set work up somewhere else: it must not drag the operator
        // off what is on screen. Three separate ways that could happen, all pinned — the active desk, the
        // selected session, and where the pane actually ends up.
        var here = _Desk("Here", WorkspaceType.Sessions);
        var elsewhere = _Desk("Release", WorkspaceType.Sessions);
        var (gateway, cockpit, _) = Dispatcher.UIThread.Invoke(() => _Gateway(_Settings(here, elsewhere)));
        var watching = Dispatcher.UIThread.Invoke(() =>
        {
            var session = new SessionViewModel { WorkspaceId = here.Id, Title = "What the operator is reading" };
            cockpit.Sessions.Add(session);
            cockpit.SelectedSession = session;
            Assert.Equal(here.Id, cockpit.Workspaces.Settings.Active?.Id);
            return session;
        });

        var result = await gateway.SpawnAsync(_Request(elsewhere.Id));

        Assert.True(result.Ok, result.Error);
        Assert.NotNull(result.PaneId);
        var spawned = Dispatcher.UIThread.Invoke(() => cockpit.FindSession(result.PaneId!));
        Assert.NotNull(spawned);
        Assert.Equal(elsewhere.Id, spawned!.WorkspaceId);

        Dispatcher.UIThread.Invoke(() =>
        {
            Assert.Equal(here.Id, cockpit.Workspaces.Settings.Active?.Id);
            Assert.Same(watching, cockpit.SelectedSession);
        });
    }

    [Fact]
    public async Task ASpawn_RecordsOneStartEntry_CarryingCallerTargetWorkspaceProfileAndWorkingDirectory()
    {
        // Criterion 5 names four things the trail must carry. One entry, not two: the refusal path and the
        // success path must not both fire for the same request.
        var desk = _Desk("Release", WorkspaceType.Sessions);
        var (gateway, _, trail) = Dispatcher.UIThread.Invoke(() => _Gateway(_Settings(desk)));

        var result = await gateway.SpawnAsync(_Request(desk.Id) with { WorkingDirectory = @"C:\repo", SessionName = "AC-545" });

        Assert.True(result.Ok, result.Error);

        var entry = Assert.Single(trail.Entries);
        Assert.Equal(AssistantSpawnAction.Start, entry.Action);
        Assert.Null(entry.Refusal);
        Assert.Equal(SpawnCaller.Assistant, entry.Caller);
        Assert.Null(entry.CallerPaneId);
        Assert.Equal(desk.Id, entry.WorkspaceId);
        Assert.Equal("Release", entry.WorkspaceName);
        Assert.Equal(ProfileLabel, entry.Profile);
        Assert.Equal(@"C:\repo", entry.WorkingDirectory);
        Assert.Equal(result.PaneId, entry.PaneId);
        Assert.Equal("AC-545", entry.SessionName);
    }

    [Fact]
    public async Task ListWorkspaces_ReportsEveryDeskTheCockpitHas_IncludingTheEmptyOnes()
    {
        // The half a session roster cannot show: a desk with nothing running on it is exactly where a spawn is
        // most likely to be wanted, and it has no session to be inferred from.
        var busy = _Desk("Here", WorkspaceType.Sessions);
        var empty = _Desk("Empty", WorkspaceType.Sessions);
        var (gateway, cockpit, _) = Dispatcher.UIThread.Invoke(() =>
        {
            var built = _Gateway(_Settings(busy, empty, _Desk("Monitoring", WorkspaceType.Dashboard)));
            built.Cockpit.Sessions.Add(new SessionViewModel { WorkspaceId = busy.Id, Title = "AC-545" });
            return built;
        });

        var rows = await gateway.ListWorkspacesAsync();

        // Derived from the settings the cockpit actually holds, not from a list repeated here.
        Assert.Equal(
            Dispatcher.UIThread.Invoke(() => cockpit.Workspaces.Settings.Workspaces.Select(workspace => workspace.Id).ToArray()),
            rows.Select(row => row.Id));
        Assert.Equal(1, rows.Single(row => row.Id == busy.Id).SessionCount);
        Assert.Equal(0, rows.Single(row => row.Id == empty.Id).SessionCount);
        Assert.True(rows.Single(row => row.Id == busy.Id).IsActive);
        Assert.False(rows.Single(row => row.Id == empty.Id).IsActive);
    }

    [Fact]
    public async Task ListWorkspaces_MarksOnlyASessionsDeskAsAbleToHostASession()
    {
        // Reported rather than left for the assistant to infer from the type id, and derived here from the same
        // open set WorkspaceType itself defines.
        var desks = _AllKnownWorkspaceTypes().Select((type, index) => _Desk($"Desk {index}", type)).ToArray();
        var (gateway, cockpit, _) = Dispatcher.UIThread.Invoke(() => _Gateway(_Settings(desks)));

        var rows = await gateway.ListWorkspacesAsync();

        var typesById = Dispatcher.UIThread.Invoke(
            () => cockpit.Workspaces.Settings.Workspaces.ToDictionary(w => w.Id, w => w.Type));
        // The type id itself, not the record's ToString() — a live transcript showed the model receiving
        // "WorkspaceType { Id = Sessions, IsBuiltIn = True }", which is a dump of the struct rather than the
        // "sessions" this row's own contract promises.
        Assert.All(rows, row => Assert.Equal(typesById[row.Id].Id, row.Type));
        Assert.All(rows, row => Assert.Equal(typesById[row.Id] == WorkspaceType.Sessions, row.CanHostSessions));
        Assert.Contains(rows, row => row.CanHostSessions);
        Assert.Contains(rows, row => !row.CanHostSessions);
    }

    [Fact]
    public async Task ListWorkspaces_NeverCountsTheAssistantItself_EvenWhenItIsStampedOntoADesk()
    {
        // The assistant has no pane in the roster it is reading, and it must not appear in its own. Stamped onto
        // a desk on purpose and left looking like an ordinary agent session in every other way, so the only thing
        // that can keep it out of the count is the check on its identity.
        var desk = _Desk("Release", WorkspaceType.Sessions);
        var (gateway, _, _) = Dispatcher.UIThread.Invoke(() =>
        {
            var built = _Gateway(_Settings(desk));
            var assistant = new SessionViewModel { WorkspaceId = desk.Id, Title = "The assistant" };
            assistant.AdoptPaneId(AssistantIdentity.PaneId);
            built.Cockpit.Sessions.Add(assistant);
            var ordinary = new SessionViewModel { WorkspaceId = desk.Id, Title = "AC-545" };
            built.Cockpit.Sessions.Add(ordinary);
            return built;
        });

        var rows = await gateway.ListWorkspacesAsync();

        Assert.Equal(1, Assert.Single(rows).SessionCount);
    }

    [Fact]
    public async Task ARefusedSpawn_IsNotADeadEnd_TheGatewayServesTheVeryNextCallNormally()
    {
        // Criterion 7. A refusal is a sentence the assistant says and then carries on from; nothing about it may
        // leave the gateway unusable, which is what a cached failure or a half-applied state would do.
        var desk = _Desk("Release", WorkspaceType.Sessions);
        var (gateway, cockpit, trail) = Dispatcher.UIThread.Invoke(() => _Gateway(_Settings(desk)));

        var refused = await gateway.SpawnAsync(_Request("no-such-desk"));
        Assert.False(refused.Ok);

        // The list still answers, and answers the truth rather than something the refusal left behind.
        var rows = await gateway.ListWorkspacesAsync();
        Assert.Equal(desk.Id, Assert.Single(rows).Id);

        // And a valid spawn straight after the refusal still starts.
        var started = await gateway.SpawnAsync(_Request(desk.Id));
        Assert.True(started.Ok, started.Error);
        Assert.NotNull(Dispatcher.UIThread.Invoke(() => cockpit.FindSession(started.PaneId!)));

        // Both outcomes on the trail, in order, so the refusal is visible next to the spawn that followed it.
        Assert.Equal(2, trail.Entries.Count);
        Assert.NotNull(trail.Entries[0].Refusal);
        Assert.Null(trail.Entries[1].Refusal);
    }

    [Fact]
    public async Task ARefusedStop_IsNotADeadEnd_TheNextStopStillWorks()
    {
        var desk = _Desk("Release", WorkspaceType.Sessions);
        var (gateway, cockpit, trail) = Dispatcher.UIThread.Invoke(() => _Gateway(_Settings(desk)));
        var session = Dispatcher.UIThread.Invoke(() =>
        {
            var added = new SessionViewModel { WorkspaceId = desk.Id, Title = "AC-545" };
            cockpit.Sessions.Add(added);
            return added;
        });

        Assert.False((await gateway.StopAsync(AssistantIdentity.PaneId)).Ok);

        var stopped = await gateway.StopAsync(session.PaneId);

        Assert.True(stopped.Ok, stopped.Error);
        Assert.Equal("AC-545", stopped.SessionName);
        Dispatcher.UIThread.Invoke(
            () => Assert.DoesNotContain(cockpit.Sessions, live => live.PaneId == session.PaneId));
        Assert.Equal(2, trail.Entries.Count);
        Assert.Null(trail.Entries[1].Refusal);
    }

    // --- Removing a desk: the counterpart of create_workspace, and the tab's own ✕ ----------------------------

    [Fact]
    public async Task RemovingADeskWithSessionsOnIt_IsRefused_AndNeitherTheDeskNorItsSessionsAreTouched()
    {
        // The guarantee the tool's description makes, attacked directly. Closing a desk stops everything on it in
        // one go, and what the operator approved was a row naming a desk — so the sessions go first, through
        // stop_agent and an approval each. Both halves are asserted: the reason, and that nothing was already
        // half-done by the time it came back.
        var here = _Desk("Here", WorkspaceType.Sessions);
        var busy = _Desk("Henk", WorkspaceType.Sessions);
        var (gateway, cockpit, _) = Dispatcher.UIThread.Invoke(() =>
        {
            var built = _Gateway(_Settings(here, busy));
            built.Cockpit.Sessions.Add(new SessionViewModel { WorkspaceId = busy.Id, Title = "AC-545" });
            built.Cockpit.Sessions.Add(new SessionViewModel { WorkspaceId = busy.Id, Title = "AC-546" });
            return built;
        });

        var result = await gateway.RemoveWorkspaceAsync(busy.Id);

        Assert.False(result.Ok);
        Assert.Equal("There are still 2 sessions on 'Henk'. Stop them first — I do not close a desk with work still on it.", result.Error);

        Dispatcher.UIThread.Invoke(() =>
        {
            Assert.Contains(cockpit.Workspaces.Settings.Workspaces, workspace => workspace.Id == busy.Id);
            Assert.Equal(2, cockpit.Sessions.Count(session => session.WorkspaceId == busy.Id));
        });
    }

    [Fact]
    public async Task RemovingADeskWithExactlyOneSessionOnIt_SaysOneSession_NotOneSessions()
    {
        // The singular is not a nicety: this reason is read out loud, and "there are still 1 sessions" is the
        // sentence that tells the operator the assistant is reciting a template rather than looking at their desk.
        var here = _Desk("Here", WorkspaceType.Sessions);
        var busy = _Desk("Henk", WorkspaceType.Sessions);
        var (gateway, _, _) = Dispatcher.UIThread.Invoke(() =>
        {
            var built = _Gateway(_Settings(here, busy));
            built.Cockpit.Sessions.Add(new SessionViewModel { WorkspaceId = busy.Id, Title = "AC-545" });
            return built;
        });

        var result = await gateway.RemoveWorkspaceAsync(busy.Id);

        Assert.False(result.Ok);
        Assert.Equal("There is still 1 session on 'Henk'. Stop it first — I do not close a desk with work still on it.", result.Error);
    }

    [Fact]
    public async Task RemovingADeskThatHoldsOnlyAPlainTerminal_IsStillRefused_ThoughTheRosterCountsItAsEmpty()
    {
        // The one place this check may not simply reuse list_workspaces' number. A terminal is not an agent
        // session, so the roster reports the desk as empty — but closing the desk kills its pty just the same, and
        // that is precisely the work nobody asked to lose. Both facts are pinned in one test, because the second
        // only means something next to the first.
        var here = _Desk("Here", WorkspaceType.Sessions);
        var withATerminal = _Desk("Henk", WorkspaceType.Sessions);
        var (gateway, cockpit, _) = Dispatcher.UIThread.Invoke(() =>
        {
            var built = _Gateway(_Settings(here, withATerminal));
            built.Cockpit.Sessions.Add(new TtyViewModel { WorkspaceId = withATerminal.Id, Title = "pwsh", ShowPluginHeaderItems = false });
            return built;
        });

        Assert.Equal(0, (await gateway.ListWorkspacesAsync()).Single(row => row.Id == withATerminal.Id).SessionCount);

        var result = await gateway.RemoveWorkspaceAsync(withATerminal.Id);

        Assert.False(result.Ok);
        Assert.Contains("still 1 session on 'Henk'", result.Error);
        Dispatcher.UIThread.Invoke(
            () => Assert.Contains(cockpit.Workspaces.Settings.Workspaces, workspace => workspace.Id == withATerminal.Id));
    }

    [Fact]
    public async Task RemovingAnEmptyDesk_TakesItOutOfTheWorkspacesTheCockpitHolds_AndNamesWhatWent()
    {
        // The other side of every refusal above: an empty desk really does go, out of the same settings the tab
        // strip renders, and the name comes back so the assistant can say which tab the operator just lost.
        var here = _Desk("Here", WorkspaceType.Sessions);
        var empty = _Desk("Henk", WorkspaceType.Sessions);
        var (gateway, cockpit, _) = Dispatcher.UIThread.Invoke(() => _Gateway(_Settings(here, empty)));

        var result = await gateway.RemoveWorkspaceAsync(empty.Id);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("Henk", result.Name);
        Dispatcher.UIThread.Invoke(() => Assert.Equal(
            [here.Id],
            cockpit.Workspaces.Settings.Workspaces.Select(workspace => workspace.Id)));
    }

    /// <summary>
    /// Only a sessions desk. Every other type is refused outright and stays exactly where it was.
    /// </summary>
    /// <remarks>
    /// The occupancy check counts sessions, and a desk of another type has none — so a dashboard holding a full
    /// screen of widgets read as empty and was closed on the spot, taking a layout nobody was shown and nothing can
    /// rebuild. Warning about it was not the fix: a consent card shows one line of text and cannot enumerate what a
    /// close would destroy, so the tool is narrower than the ✕ instead of pretending to be the same act.
    /// <para>
    /// Driven off <see cref="WorkspaceType"/> itself, like the spawn refusal above and for the same reason: the set
    /// is open, and a hand-written list of "dashboard and projects" would stay green on the day a plugin desk
    /// arrives. A second desk is present only so the last-desk rule is not what refuses.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(EveryWorkspaceTypeThatCannotHostASession))]
    public async Task RemovingADeskThatIsNotASessionsDesk_IsRefused_AndTheDeskIsStillThere(string workspaceTypeId)
    {
        var here = _Desk("Here", WorkspaceType.Sessions);
        var other = _Desk("Monitoring", WorkspaceType.FromId(workspaceTypeId));
        var (gateway, cockpit, _) = Dispatcher.UIThread.Invoke(() => _Gateway(_Settings(here, other)));

        var result = await gateway.RemoveWorkspaceAsync(other.Id);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Dispatcher.UIThread.Invoke(
            () => Assert.Contains(cockpit.Workspaces.Settings.Workspaces, workspace => workspace.Id == other.Id));
    }

    /// <summary>
    /// And the reason says which kind of desk it is, in the type's own id — the same string
    /// <c>list_workspaces</c> hands back, so what the assistant was told a desk is and why it will not close it
    /// cannot disagree. Asserted on a dashboard because the projects overview is refused one rule earlier, by its
    /// own more specific sentence.
    /// </summary>
    [Fact]
    public async Task TheRefusalForANonSessionsDesk_NamesTheTypeTheRosterReportedForIt()
    {
        var here = _Desk("Here", WorkspaceType.Sessions);
        var dashboard = _Desk("Monitoring", WorkspaceType.Dashboard);
        var (gateway, _, _) = Dispatcher.UIThread.Invoke(() => _Gateway(_Settings(here, dashboard)));

        var reportedType = (await gateway.ListWorkspacesAsync()).Single(row => row.Id == dashboard.Id).Type;
        var result = await gateway.RemoveWorkspaceAsync(dashboard.Id);

        Assert.False(result.Ok);
        Assert.Contains(reportedType, result.Error);
        Assert.Contains("Monitoring", result.Error);
    }

    [Fact]
    public async Task RemovingTheOnlyDeskLeft_IsRefused_BecauseTheCockpitAlwaysNeedsOneToShow()
    {
        // CanClose is the tab's own gate, asked here rather than re-derived — this is the half of it that has
        // nothing to do with what is on the desk.
        var only = _Desk("Henk", WorkspaceType.Sessions);
        var (gateway, cockpit, _) = Dispatcher.UIThread.Invoke(() => _Gateway(_Settings(only)));

        var result = await gateway.RemoveWorkspaceAsync(only.Id);

        Assert.False(result.Ok);
        Assert.Equal("'Henk' is the only desk left, and the cockpit always needs one to show.", result.Error);
        Dispatcher.UIThread.Invoke(() => Assert.Single(cockpit.Workspaces.Settings.Workspaces));
    }

    [Fact]
    public async Task RemovingTheProjectsOverview_IsRefused_AndSaysWhyRatherThanCountingSessions()
    {
        // The other half of CanClose, and a different sentence on purpose: the projects overview is a fixture, so
        // "stop what is on it first" would send the operator off to do something that cannot help.
        var projects = _Desk("Projects", WorkspaceType.Projects);
        var (gateway, cockpit, _) = Dispatcher.UIThread.Invoke(
            () => _Gateway(_Settings(_Desk("Here", WorkspaceType.Sessions), projects)));

        var result = await gateway.RemoveWorkspaceAsync(projects.Id);

        Assert.False(result.Ok);
        Assert.Contains("projects overview", result.Error);
        Dispatcher.UIThread.Invoke(
            () => Assert.Contains(cockpit.Workspaces.Settings.Workspaces, workspace => workspace.Id == projects.Id));
    }

    [Fact]
    public async Task RemovingAWorkspaceIdThatNamesNothing_IsRefusedWithAReason_NotAnException()
    {
        var (gateway, _, _) = Dispatcher.UIThread.Invoke(
            () => _Gateway(_Settings(_Desk("Here", WorkspaceType.Sessions))));

        var result = await gateway.RemoveWorkspaceAsync("no-such-desk");

        Assert.False(result.Ok);
        Assert.Contains("no workspace with id 'no-such-desk'", result.Error);
    }

    [Fact]
    public async Task ADeskThatWasRefusedWhileBusy_IsRemovedOnceItsSessionsAreStopped()
    {
        // The order the tool's description sends the assistant round: refuse, stop, ask again. Without this the
        // refusals above would all pass just as happily on a gateway that never removes anything at all.
        var here = _Desk("Here", WorkspaceType.Sessions);
        var busy = _Desk("Henk", WorkspaceType.Sessions);
        var (gateway, cockpit, _) = Dispatcher.UIThread.Invoke(() =>
        {
            var built = _Gateway(_Settings(here, busy));
            built.Cockpit.Sessions.Add(new SessionViewModel { WorkspaceId = busy.Id, Title = "AC-545" });
            return built;
        });
        var session = Dispatcher.UIThread.Invoke(() => cockpit.Sessions.Single(pane => pane.WorkspaceId == busy.Id));

        Assert.False((await gateway.RemoveWorkspaceAsync(busy.Id)).Ok);

        Assert.True((await gateway.StopAsync(session.PaneId)).Ok);
        var removed = await gateway.RemoveWorkspaceAsync(busy.Id);

        Assert.True(removed.Ok, removed.Error);
        Dispatcher.UIThread.Invoke(() => Assert.DoesNotContain(
            cockpit.Workspaces.Settings.Workspaces, workspace => workspace.Id == busy.Id));
    }

    // --- AC-592: renaming a session and a desk -----------------------------------------------------------------

    [Fact]
    public async Task RenamingARunningSession_TakesTheName_AndItCountsAsOneSomebodyChose()
    {
        // Criteria 1 and 2 in one test, because the second is the half that is easy to ship broken: a rename that
        // only set the title would look right on screen and be silently replaced the next time a plugin links a
        // ticket to that session (#AC-310).
        var desk = _Desk("Release", WorkspaceType.Sessions);
        var (gateway, cockpit, trail) = Dispatcher.UIThread.Invoke(() => _Gateway(_Settings(desk)));
        var session = Dispatcher.UIThread.Invoke(() =>
        {
            var added = new SessionViewModel { WorkspaceId = desk.Id, Title = "Claude · 10:31" };
            cockpit.Sessions.Add(added);
            return added;
        });

        var result = await gateway.RenameSessionAsync(session.PaneId, "  Luna  ");

        Assert.True(result.Ok, result.Error);
        Assert.Equal("Luna", result.Name);
        Dispatcher.UIThread.Invoke(() =>
        {
            Assert.Equal("Luna", session.Title);
            Assert.False(session.HasGeneratedName);
            Assert.False(cockpit.SuggestSessionName(session.PaneId, "AC-592"));
            Assert.Equal("Luna", session.Title);
        });

        // Deliberately not on the spawn trail: that record exists for what costs money and starts processes.
        Assert.Empty(trail.Entries);
    }

    [Fact]
    public async Task RenamingAPaneIdNothingIsRunningUnder_IsRefusedWithAReason()
    {
        var (gateway, _, _) = Dispatcher.UIThread.Invoke(
            () => _Gateway(_Settings(_Desk("Release", WorkspaceType.Sessions))));

        var result = await gateway.RenameSessionAsync("pane-that-closed", "Luna");

        Assert.False(result.Ok);
        Assert.Null(result.Name);
        Assert.Contains("no session with pane id 'pane-that-closed'", result.Error);
    }

    [Fact]
    public async Task RenamingTheAssistantsOwnSession_IsRefused_EvenWhenItSitsInTheRoster()
    {
        // Stamped into Sessions on purpose: the guard has to be the identity check, not the accident that the
        // assistant is in no collection today. Same shape as the stop-side test above, and for the same reason.
        var desk = _Desk("Release", WorkspaceType.Sessions);
        var (gateway, cockpit, _) = Dispatcher.UIThread.Invoke(() =>
        {
            var built = _Gateway(_Settings(desk));
            var assistant = new SessionViewModel { WorkspaceId = desk.Id, Title = "The assistant" };
            assistant.AdoptPaneId(AssistantIdentity.PaneId);
            built.Cockpit.Sessions.Add(assistant);
            return built;
        });

        var result = await gateway.RenameSessionAsync(AssistantIdentity.PaneId, "Luna");

        Assert.False(result.Ok);
        Assert.Contains("my own session", result.Error);
        Dispatcher.UIThread.Invoke(() => Assert.Equal(
            "The assistant",
            cockpit.Sessions.Single(session => session.PaneId == AssistantIdentity.PaneId).Title));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RenamingASessionToNothing_IsRefused_AndLeavesTheNameStanding(string blank)
    {
        var desk = _Desk("Release", WorkspaceType.Sessions);
        var (gateway, cockpit, _) = Dispatcher.UIThread.Invoke(() => _Gateway(_Settings(desk)));
        var session = Dispatcher.UIThread.Invoke(() =>
        {
            var added = new SessionViewModel { WorkspaceId = desk.Id, Title = "AC-592" };
            cockpit.Sessions.Add(added);
            return added;
        });

        var result = await gateway.RenameSessionAsync(session.PaneId, blank);

        Assert.False(result.Ok);
        Assert.Contains("needs a name", result.Error);
        Dispatcher.UIThread.Invoke(() => Assert.Equal("AC-592", session.Title));
    }

    [Fact]
    public async Task RenamingADesk_ChangesItsName_AndLeavesTheOperatorOnTheDeskTheyWereLookingAt()
    {
        // Renaming a desk is not walking to it: the desk being renamed is deliberately not the active one.
        var here = _Desk("Here", WorkspaceType.Sessions);
        var elsewhere = _Desk("Release", WorkspaceType.Sessions);
        var (gateway, cockpit, _) = Dispatcher.UIThread.Invoke(() => _Gateway(_Settings(here, elsewhere)));

        var result = await gateway.RenameWorkspaceAsync(elsewhere.Id, "  Luna's desk  ");

        Assert.True(result.Ok, result.Error);
        Assert.Equal("Luna's desk", result.Name);
        Dispatcher.UIThread.Invoke(() =>
        {
            var settings = cockpit.Workspaces.Settings;
            Assert.Equal("Luna's desk", settings.Workspaces.Single(workspace => workspace.Id == elsewhere.Id).Name);
            Assert.Equal("Here", settings.Workspaces.Single(workspace => workspace.Id == here.Id).Name);
            Assert.Equal(here.Id, settings.Active?.Id);
        });
    }

    [Fact]
    public async Task RenamingAWorkspaceIdThatNamesNothing_IsRefusedWithAReason()
    {
        var (gateway, _, _) = Dispatcher.UIThread.Invoke(
            () => _Gateway(_Settings(_Desk("Release", WorkspaceType.Sessions))));

        var result = await gateway.RenameWorkspaceAsync("no-such-desk", "Luna's desk");

        Assert.False(result.Ok);
        Assert.Contains("no workspace with id 'no-such-desk'", result.Error);
    }

    [Fact]
    public async Task RenamingADeskToNothing_IsRefused_AndLeavesTheNameStanding()
    {
        var desk = _Desk("Release", WorkspaceType.Sessions);
        var (gateway, cockpit, _) = Dispatcher.UIThread.Invoke(() => _Gateway(_Settings(desk)));

        var result = await gateway.RenameWorkspaceAsync(desk.Id, "   ");

        Assert.False(result.Ok);
        Assert.Contains("needs a name", result.Error);
        Dispatcher.UIThread.Invoke(() => Assert.Equal("Release", Assert.Single(cockpit.Workspaces.Settings.Workspaces).Name));
    }

    // --- AC-1018: ask_structured_question's card actually rendering, not just the tool call succeeding ---------

    /// <summary>
    /// AC-955 built <c>ask_structured_question</c> and verified it only with a headless screenshot scene that
    /// hand-builds a <c>Kind = ToolUse</c> fixture — never the gateway's own construction path. Live, the card
    /// never appeared: only the plain question text did (AC-1018). This drives the exact production method an MCP
    /// call reaches, through a real Avalonia dispatcher, onto a real <see cref="SessionViewModel"/> — the same
    /// object <c>AssistantChatViewModel</c> binds its transcript from — and checks the same two properties
    /// <c>TranscriptRowView.axaml</c> actually gates the card on, not just that a row landed on <c>Transcript</c>.
    /// </summary>
    [Fact]
    public async Task AskStructuredQuestion_PutsARow_ThatRendersAsTheCard_NotAsPlainText()
    {
        var (gateway, session) = Dispatcher.UIThread.Invoke(() =>
        {
            var realSession = new SessionViewModel();
            var host = Substitute.For<IAssistantSessionHost>();
            host.Session.Returns(realSession);

            var profiles = Substitute.For<ISessionProfileStore>();
            profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SessionProfile>>([]));

            var built = new AssistantAgentGateway(
                _Cockpit(),
                profiles,
                Substitute.For<IAssistantSpawnAuditLog>(),
                Substitute.For<IWorkspaceAgentGateway>(),
                Substitute.For<IAgentMessageInbox>(),
                Substitute.For<IAgentNotifyAuditLog>(),
                Substitute.For<IPluginProviderRegistry>(),
                new SessionWatcher(Substitute.For<IAgentMessageInbox>()),
                host);
            return (built, realSession);
        });

        var result = await gateway.AskStructuredQuestionAsync(
            "Which profile?",
            [("Programmer (Opus)", null), ("Programmer (Sonnet)", null)],
            multiSelect: false,
            allowOther: true,
            header: null);

        Assert.True(result.Ok);

        var entry = Dispatcher.UIThread.Invoke(() => session.Transcript.Single(row => row.Kind == TranscriptEntryKind.Question));
        Assert.True(entry.HasQuestionPrompts);
        // The two gates TranscriptRowView.axaml actually reads: ToolBranch must carry the row (it hosts the
        // nested QuestionBranch card), and IsPlainNonMarkdown must be false (else the raw text renders instead).
        Assert.NotNull(entry.ToolBranch);
        Assert.False(entry.IsPlainNonMarkdown);
        // And that it reaches the collection AssistantChatViewModel actually binds — not a copy.
        Assert.Contains(entry, Dispatcher.UIThread.Invoke(() => session.VisibleTranscript));
    }

    // --- The graph under test -------------------------------------------------------------------------------

    /// <summary>
    /// Every workspace type the source itself knows about, read off <see cref="WorkspaceType"/>'s own static
    /// members, plus one plugin-registered type — because that type is a record struct over a string and not an
    /// enum precisely so the set stays open, and a rule about "types that cannot host a session" has to hold for
    /// the ones no test author has seen.
    /// </summary>
    private static IReadOnlyList<WorkspaceType> _AllKnownWorkspaceTypes() =>
    [
        .. typeof(WorkspaceType)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(WorkspaceType))
            .Select(property => (WorkspaceType)property.GetValue(null)!),
        WorkspaceType.FromId("acme.autopilot"),
    ];

    private static AgentSpawnRequest _Request(string workspaceId) =>
        new(SpawnTarget.NamedByTheAssistant(workspaceId), ProfileLabel);

    private static Workspace _Desk(string name, WorkspaceType type) => Workspace.Create(name, type);

    private static WorkspaceSettings _Settings(params Workspace[] workspaces) =>
        new() { Workspaces = workspaces, ActiveWorkspaceId = workspaces[0].Id };

    /// <summary>
    /// Built on the UI thread by every caller: <c>CockpitViewModel</c> and its collections belong to the thread
    /// the headless platform owns, and the gateway marshals onto that same thread when it is called.
    /// </summary>
    private static (AssistantAgentGateway Gateway, CockpitViewModel Cockpit, RecordingSpawnTrail Trail) _Gateway(
        WorkspaceSettings settings,
        CockpitViewModel? host = null,
        // The profile a spawn runs under and the registry its provider is resolved through — defaulted to a plain
        // Claude-config profile, which is what every test that is not about provider options wants.
        SessionProfile? profile = null,
        IPluginProviderRegistry? pluginProviders = null,
        IWorktreeManager? worktreeManager = null)
    {
        var cockpit = host ?? _Cockpit();
        cockpit.Workspaces.Settings = settings;

        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SessionProfile>>(
            [profile ?? new SessionProfile(ProfileLabel, new ClaudeConfig(@"C:\fake\.claude"))]));

        var trail = new RecordingSpawnTrail();
        return (
            new AssistantAgentGateway(
                cockpit,
                profiles,
                trail,
                Substitute.For<IWorkspaceAgentGateway>(),
                Substitute.For<IAgentMessageInbox>(),
                Substitute.For<IAgentNotifyAuditLog>(),
                pluginProviders ?? Substitute.For<IPluginProviderRegistry>(),
                new SessionWatcher(Substitute.For<IAgentMessageInbox>()),
                Substitute.For<IAssistantSessionHost>(),
                worktreeManager),
            cockpit,
            trail);
    }

    /// <summary>
    /// The gateway over a given set of profiles and provider registry — <see cref="AssistantAgentGateway.ListProfilesAsync"/>
    /// reads only those two and never the desks, so this skips the workspace settings the spawn tests need.
    /// </summary>
    private static AssistantAgentGateway _GatewayOverProfiles(
        IPluginProviderRegistry pluginProviders, params SessionProfile[] known)
    {
        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SessionProfile>>(known));

        return new AssistantAgentGateway(
            _Cockpit(),
            profiles,
            new RecordingSpawnTrail(),
            Substitute.For<IWorkspaceAgentGateway>(),
            Substitute.For<IAgentMessageInbox>(),
            Substitute.For<IAgentNotifyAuditLog>(),
            pluginProviders,
            new SessionWatcher(Substitute.For<IAgentMessageInbox>()),
            Substitute.For<IAssistantSessionHost>());
    }

    // A plugin-backed profile under the test's own label, with whatever start defaults the caller wants on it —
    // the only shape that resolves to a provider registration, and so the only one an override can be checked against.
    private static SessionProfile _PluginProfile(params (string Key, string Value)[] optionDefaults) =>
        new(ProfileLabel, new PluginProviderConfig("claude", "{}"))
        {
            Defaults = new ProfileDefaults(string.Empty, string.Empty, string.Empty, AutoApproveTools: false)
            {
                OptionDefaults = optionDefaults.ToDictionary(option => option.Key, option => option.Value),
            },
        };

    // Claude's three declared options (AC-649), as the real plugin registers them.
    private static IPluginProviderRegistry _ClaudeRegistry()
    {
        var registry = Substitute.For<IPluginProviderRegistry>();
        registry.Resolve("claude").Returns(_ProviderRegistration(
            "claude",
            "Claude",
            new PluginSessionOptionDescriptor("permission-mode", "Permission mode",
                [new PluginSessionOptionValue("default", "Ask permissions"), new PluginSessionOptionValue("bypassPermissions", "Bypass permissions")], "default"),
            new PluginSessionOptionDescriptor("model", "Model"),
            new PluginSessionOptionDescriptor("effort", "Effort",
                [new PluginSessionOptionValue("low", "Low"), new PluginSessionOptionValue("high", "High")], "medium")));
        return registry;
    }

    private static SessionProviderRegistration _ProviderRegistration(
        string providerId, string displayName, params PluginSessionOptionDescriptor[] declaredOptions) =>
        new(
            providerId,
            displayName,
            _ => throw new NotSupportedException("Nothing here starts a session."),
            new PluginSessionCapabilities(SupportsTools: true, SupportsPermissions: true) { DeclaredOptions = declaredOptions },
            _ => throw new NotSupportedException("Nothing here opens the profile editor."));

    /// <summary>
    /// A host with no session factories behind it — the parameterless constructor's graph, which is the one
    /// state in which a launch declines and returns nothing. Its three sample panels are cleared: they are
    /// design-time furniture and would be counted as running sessions.
    /// </summary>
    private static CockpitViewModel _CockpitWithoutSessionMachinery()
    {
        var cockpit = new CockpitViewModel();
        cockpit.Sessions.Clear();
        return cockpit;
    }

    /// <summary>
    /// The same minimal host graph <c>CockpitViewModelTests</c> builds: real session collections and a real
    /// <c>WorkspacesViewModel</c> (no store, so nothing persists), which is all the gateway reads.
    /// </summary>
    private static CockpitViewModel _Cockpit()
    {
        var notificationSettingsStore = Substitute.For<INotificationSettingsStore>();
        notificationSettingsStore.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplaySettingsStore = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplaySettingsStore.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehaviorSettingsStore = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehaviorSettingsStore.LoadAsync().Returns(new SessionBehaviorSettings());
        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync().Returns(new LayoutSettings());
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync().Returns(new VoiceSettings());
        var terminalSettingsStore = Substitute.For<ITerminalSettingsStore>();
        terminalSettingsStore.LoadAsync().Returns(new TerminalSettings());

        return new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            Substitute.For<ISessionDialogService>(),
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notificationSettingsStore,
            transcriptDisplaySettingsStore,
            sessionBehaviorSettingsStore,
            layoutSettingsStore,
            voiceSettingsStore,
            terminalSettingsStore);
    }

    /// <summary>
    /// The spawn trail, kept in a list. Every refusal assertion here is an assertion about what was written, so
    /// the fake holds the entries themselves rather than counting calls.
    /// </summary>
    private sealed class RecordingSpawnTrail : IAssistantSpawnAuditLog
    {
        public List<AssistantSpawnAuditEntry> Entries { get; } = [];

        public Task RecordAsync(AssistantSpawnAuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AssistantSpawnAuditEntry>> ReadRecentAsync(int limit = 200, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AssistantSpawnAuditEntry>>([.. Enumerable.Reverse(Entries)]);
    }
}
