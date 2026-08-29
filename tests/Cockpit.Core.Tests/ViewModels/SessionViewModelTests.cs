using System.Runtime.CompilerServices;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Voice;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Exercises <see cref="SessionViewModel"/>'s transcript-shaping logic (the "Thinking..." row
/// lifecycle) and the configured start path (<see cref="SessionViewModel.StartConfiguredAsync"/>,
/// which the New-session dialog drives) against a fake <see cref="ISessionDriver"/>. <c>Apply</c> is
/// invoked directly (it is <c>internal</c>, visible via <c>InternalsVisibleTo</c>) rather than through
/// <c>ConsumeEventsAsync</c>'s dispatcher, since no Avalonia dispatcher is initialized in this host.
/// </summary>
public class SessionViewModelTests
{
    private static readonly SessionProfile Profile = new("default", new ClaudeConfig(@"C:\fake\.claude"));

    [Fact]
    public async Task StartConfigured_LaunchesWithTheChosenModel()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, new ModelOption("Haiku", "haiku"), SessionOptionCatalog.DefaultEffort);

        await session.Received(1).StartAsync(Profile, Arg.Any<string?>(), "haiku", Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(), Arg.Any<SessionResume?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    // AC-539: a resume the CLI cannot resolve fails on the restored pane's first turn, and the offer must come back
    // saying why — the provider's reason plus the directory it looked in, since Claude keeps its saved conversations
    // per working directory and "No conversation found" alone is a dead end.
    [Fact]
    public async Task AFailedResumeTurn_BringsTheOfferBackNamingTheWorkingDirectory()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));

        var pane = new Cockpit.Core.Workspaces.WorkspacePane(vm.PaneId, Cockpit.Core.Workspaces.PaneKind.AiSession) { ProfileId = "default" };
        var state = new SessionStateRecord(vm.PaneId, "default", "claude", "conv-1", SessionConversationIdState.Known, @"C:\gone", null, null, "default", DateTimeOffset.UtcNow);
        vm.RestoreOffer = new Cockpit.App.Services.SessionRestorePlan(
            pane, Profile, Cockpit.App.Services.SessionRestoreAvailability.Known, "This session's earlier conversation can be resumed.") { State = state };

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        vm.Apply(new TurnCompleted
        {
            SessionId = "S1",
            Subtype = "error_during_execution",
            // An error_during_execution turn carries no result — the reason is only in errors[].
            Result = null,
            IsError = true,
            Errors = ["No conversation found with session ID: conv-1"],
        });

        Assert.NotNull(vm.RestoreOffer);
        Assert.Equal(Cockpit.App.Services.SessionRestoreAvailability.Gone, vm.RestoreOffer!.Availability);
        Assert.False(vm.CanResumeConversation);
        Assert.Contains("No conversation found with session ID: conv-1", vm.RestoreDegradedReason, StringComparison.Ordinal);
        Assert.Contains(@"C:\gone", vm.RestoreDegradedReason, StringComparison.Ordinal);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task TurnCompleted_PullsTheDriversLimits_IntoTheHeaderBars()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var reset = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        session.CurrentStatus.Returns(new SessionStatusFeed(25, [new SessionRateWindow("5h", 60, reset), new SessionRateWindow("wk", 80, null)]));
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));
        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        // D7: a completed turn is when the provider's usage changes, so the header pulls the driver's status then.
        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        Assert.Equal(25, vm.ContextUsedPercent);
        // AC-761: _RefreshLimits now routes through ApplyUsage, so an SDK session's windows carry a threshold too
        // (the fallback 90 — this profile names no registered plugin provider to declare its own).
        Assert.Equal(new[] { new SessionRateWindow("5h", 60, reset, 90), new SessionRateWindow("wk", 80, null, 90) }, vm.RateLimits);
        Assert.Contains("Context window: 25% used", vm.LimitsTooltip);

        await vm.DisposeAsync();
    }

    // AC-775: two profiles sharing one underlying credential (same ConfigDir) but different labels must show
    // the same figure within the TTL — the regression this ticket exists to close is caching on the label
    // instead. Session B's own driver reports nothing (a sibling that has not polled yet); it must still pick
    // up A's fresh reading from the shared cache rather than showing no bars.
    [Fact]
    public async Task TwoSessions_SameCredentialDifferentLabel_ShareTheCachedUsage()
    {
        var cache = new SharedUsageCache();
        var profileA = new SessionProfile("profile-a", new ClaudeConfig(@"C:\fake\.claude"));
        var profileB = new SessionProfile("profile-b", new ClaudeConfig(@"C:\fake\.claude"));
        var reset = DateTimeOffset.FromUnixTimeSeconds(1800000000);

        var driverA = Substitute.For<ISessionDriver>();
        driverA.Events.Returns(EmptyEvents());
        driverA.CurrentStatus.Returns(new SessionStatusFeed(44, [new SessionRateWindow("5h", 70, reset)]));
        var vmA = new SessionViewModel(new SessionManager(FactoryFor(driverA)), sharedUsageCache: cache);
        await vmA.StartConfiguredAsync(
            profileA, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);
        vmA.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        var driverB = Substitute.For<ISessionDriver>();
        driverB.Events.Returns(EmptyEvents());
        var vmB = new SessionViewModel(new SessionManager(FactoryFor(driverB)), sharedUsageCache: cache);
        await vmB.StartConfiguredAsync(
            profileB, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        Assert.Equal(44, vmB.ContextUsedPercent);
        Assert.Equal(new[] { new SessionRateWindow("5h", 70, reset, 90) }, vmB.RateLimits);

        await vmA.DisposeAsync();
        await vmB.DisposeAsync();
    }

    // AC-775: two profiles on genuinely different credentials must never see each other's reading, even under
    // the same provider type.
    [Fact]
    public async Task TwoSessions_DifferentCredential_NeverShareTheCachedUsage()
    {
        var cache = new SharedUsageCache();
        var profileA = new SessionProfile("profile-a", new ClaudeConfig(@"C:\fake\.claude-a"));
        var profileC = new SessionProfile("profile-c", new ClaudeConfig(@"C:\fake\.claude-c"));
        var reset = DateTimeOffset.FromUnixTimeSeconds(1800000000);

        var driverA = Substitute.For<ISessionDriver>();
        driverA.Events.Returns(EmptyEvents());
        driverA.CurrentStatus.Returns(new SessionStatusFeed(44, [new SessionRateWindow("5h", 70, reset)]));
        var vmA = new SessionViewModel(new SessionManager(FactoryFor(driverA)), sharedUsageCache: cache);
        await vmA.StartConfiguredAsync(
            profileA, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);
        vmA.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        var driverC = Substitute.For<ISessionDriver>();
        driverC.Events.Returns(EmptyEvents());
        var vmC = new SessionViewModel(new SessionManager(FactoryFor(driverC)), sharedUsageCache: cache);
        await vmC.StartConfiguredAsync(
            profileC, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        Assert.Null(vmC.ContextUsedPercent);
        Assert.Empty(vmC.RateLimits);

        await vmA.DisposeAsync();
        await vmC.DisposeAsync();
    }

    // AC-660: reported as "the usage pill is missing entirely" on 3 of 4 open SDK panes. Root cause — a resumed
    // pane's driver can already know the resumed conversation's real usage the moment it starts (ClaudeSdkSessionDriver
    // now polls for it on resume), but before this fix the header only ever pulled CurrentStatus at a TurnCompleted
    // boundary, so a resumed pane the operator had not yet sent a fresh message to showed no pill at all — not even
    // though the driver already had real figures — until its first turn in this process finished. Only the one pane
    // the operator had actually prompted looked normal.
    [Fact]
    public async Task AResumedSession_ShowsTheDriversAlreadyKnownLimits_BeforeAnyTurnCompletes()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var reset = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        session.CurrentStatus.Returns(new SessionStatusFeed(37, [new SessionRateWindow("5h", 12, reset)]));
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort,
            resume: new SessionResume(SessionResumeMode.BySessionId, "conv-1"));

        // No TurnCompleted here — this is the resumed pane sitting idle, exactly what the operator sees on reopen.
        Assert.Equal(37, vm.ContextUsedPercent);
        Assert.Equal(new[] { new SessionRateWindow("5h", 12, reset, 90) }, vm.RateLimits);
        Assert.True(vm.HasUsagePillRegion);

        await vm.DisposeAsync();
    }

    // AC-701: AC-660 scoped the post-start pull to resume, so a fresh pane showed no pill at all until its first
    // turn completed — on a long agent turn that is tens of minutes of nothing. The driver polls at every start
    // now (measured: a fresh session answers both requests), so the header must pull for every start too.
    [Fact]
    public async Task AFreshSession_ShowsTheDriversLimits_BeforeAnyTurnCompletes()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var reset = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        session.CurrentStatus.Returns(new SessionStatusFeed(2, [new SessionRateWindow("5h", 15, reset)]));
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        Assert.Equal(2, vm.ContextUsedPercent);
        Assert.Equal(new[] { new SessionRateWindow("5h", 15, reset, 90) }, vm.RateLimits);
        Assert.True(vm.HasUsagePillRegion);

        await vm.DisposeAsync();
    }

    // AC-536: measured root cause was neither of the two candidates the ticket itself named (a dropped Usage, or
    // the "Connected (…)" status text crowding the meter out of the layout) — both were fine. The actual break was
    // a third one: SessionViewModel.SuppressCostMeter vetoed the standalone meter at every non-Developer reading
    // level (AC-138), on the assumption the usage pill would carry it instead — but the pill only carries it when
    // the operator has put UsagePillField.SessionUsage on it, which is not the default (default is ctx only). A
    // Focus-level SDK session therefore lost the token count with no reachable substitute, while a TTY session
    // (no reading level at all) always showed it. Confirmed with a throwaway harness driving the real Apply/turn
    // path at all three levels before touching any production code (Developer/Focus showed it, Simple did not —
    // matching Simple's explicit "no cost" promise, which this fix intentionally leaves alone).
    [Theory]
    [InlineData(ReadingLevel.Developer)]
    [InlineData(ReadingLevel.Focus)]
    public void TurnCompleted_WithUsage_ShowsTheTokenMeter_OnDeveloperAndFocus(ReadingLevel level)
    {
        var vm = NewVm();
        vm.ReadingLevel = level;

        vm.Apply(new TurnCompleted
        {
            SessionId = "S1", Subtype = "success", Result = "done", IsError = false,
            Usage = new TokenUsage(1_000, 2_000, 0, 0), TotalCostUsd = 0.05,
        });

        Assert.True(vm.HasUsage);
        Assert.Equal("3.0k tok · $0.0500", vm.UsageSummary);
        Assert.Contains(vm.UsagePillItems, i => i.DisplayText == "3.0k tok · $0.0500");
    }

    [Fact]
    public void TurnCompleted_WithUsage_OnSimple_KeepsTheTokenMeterHidden()
    {
        // Simple's own promise is "no cost" outright (SessionOptionCatalog.ReadingLevels) — unlike Focus, there is
        // no substitute pill segment to fall back to here, by design.
        var vm = NewVm();
        vm.ReadingLevel = ReadingLevel.Simple;

        vm.Apply(new TurnCompleted
        {
            SessionId = "S1", Subtype = "success", Result = "done", IsError = false,
            Usage = new TokenUsage(1_000, 2_000, 0, 0), TotalCostUsd = 0.05,
        });

        Assert.True(vm.HasUsage);
        Assert.DoesNotContain(vm.UsagePillItems, i => i.DisplayText == "3.0k tok · $0.0500");
    }

    [Fact]
    public void TurnCompleted_WithNoUsage_NeverShowsTheTokenMeter()
    {
        // AC-536 AC3: a provider that reports no tokens must never surface a "0 tok" meter.
        var vm = NewVm();

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false, Usage = null });

        Assert.False(vm.HasUsage);
        Assert.DoesNotContain(vm.UsagePillItems, i => i.DisplayText.Contains("tok", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartConfigured_AppliesTheChosenEffortsBudgetOnceLive()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, new EffortOption("High", "high", 24_000));

        await session.Received(1).SetMaxThinkingTokensAsync(24_000, Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task StartConfigured_InBypass_LocksThePanelPermissionMode()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.ResolvePermissionMode("bypassPermissions"), SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        Assert.True(vm.IsPermissionModeLocked);
        Assert.Equal("bypassPermissions", Assert.Single(vm.PermissionModes).Value);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task StartConfigured_WhenTheLaunchFailsInBypass_DoesNotStrandThePanelOnAPhantomLock()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        session.StartAsync(Arg.Any<SessionProfile?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(), Arg.Any<SessionResume?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("bad executable")));
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.ResolvePermissionMode("bypassPermissions"), SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        Assert.False(vm.IsPermissionModeLocked);
        Assert.Equal(new[] { "default", "acceptEdits", "plan" }, vm.PermissionModes.Select(mode => mode.Value));

        await vm.DisposeAsync();
    }

    /// <summary>
    /// A profile referencing a missing/unresolvable plugin provider (or an invalid persisted ConfigJson)
    /// makes <c>SessionDriverFactory.Create</c>/<c>OpenAiCompatPluginSessionDriverFactory.Create</c> throw
    /// loudly by design (#45). Before this fix that call sat outside StartWithProfileAsync's try, so it went
    /// unhandled and crashed with the panel already added to the sidebar (a zombie panel). It must instead
    /// degrade to the same failed-launch path a driver.StartAsync failure already takes.
    /// </summary>
    [Fact]
    public async Task StartConfigured_WhenTheDriverFactoryThrows_DegradesToAFailedStatusInsteadOfThrowing()
    {
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>())
            .Returns(_ => throw new InvalidOperationException("No plugin session provider is registered for 'gemini-provider.gemini'."));
        var vm = new SessionViewModel(new SessionManager(factory));

        var act = async () => await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.ResolvePermissionMode("bypassPermissions"), SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        await act();
        Assert.Contains("Failed to start", vm.Status);
        // The same "leave no phantom lock" cleanup StartConfiguredAsync already runs when the launch fails
        // in bypass mode (see the test above) only fires when _eventLoopTask stayed null — proving the
        // failure took the caught path, not an unhandled throw.
        Assert.False(vm.IsPermissionModeLocked);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task StartConfigured_InALiveMode_LeavesThePermissionModeUnlocked()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.ResolvePermissionMode("plan"), SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        Assert.False(vm.IsPermissionModeLocked);
        Assert.Equal(new[] { "default", "acceptEdits", "plan" }, vm.PermissionModes.Select(mode => mode.Value));

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task StartConfigured_LocalToolSession_SeedsAutoApproveToolsFromTheProfileDefault()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        session.Capabilities.Returns(new SessionCapabilities(
            SupportsTools: true, SupportsPermissions: false, SupportsLiveModelSwitch: false, SupportsPlanMode: false, SupportsThinking: false));
        var localProfile = new SessionProfile(
            "ollama",
            new OllamaConfig("http://localhost:11434", "llama3.1"),
            Defaults: new ProfileDefaults("default", "sonnet", "medium", AutoApproveTools: true));
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));

        await vm.StartConfiguredAsync(
            localProfile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        Assert.True(vm.ShowToolAutoApprove);
        Assert.True(vm.AutoApproveTools);
        await session.Received(1).SetAutoApproveToolsAsync(true, Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task StartConfigured_LocalToolSession_WithoutTheProfileDefault_LeavesAutoApproveToolsOff()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        session.Capabilities.Returns(new SessionCapabilities(
            SupportsTools: true, SupportsPermissions: false, SupportsLiveModelSwitch: false, SupportsPlanMode: false, SupportsThinking: false));
        var localProfile = new SessionProfile(
            "ollama",
            new OllamaConfig("http://localhost:11434", "llama3.1"));
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));

        await vm.StartConfiguredAsync(
            localProfile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        Assert.True(vm.ShowToolAutoApprove);
        Assert.False(vm.AutoApproveTools);
        await session.DidNotReceive().SetAutoApproveToolsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task StartConfigured_PopulatesTheLiveControls_FromTheDriversLiveOptions()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        session.LiveOptions.Returns(
        [
            new SessionLiveOption("model", "Model", ["gpt-5-codex", "gpt-5"], "gpt-5-codex"),
            new SessionLiveOption("effort", "Effort", ["low", "medium", "high"], null),
        ]);
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        // D4: the provider's live controls become the header's generic panel, each opened on its current value.
        Assert.True(vm.HasLiveControls);
        Assert.Equal(2, System.Linq.Enumerable.Count(vm.LiveControls));
        Assert.Equal("model", vm.LiveControls[0].Key);
        Assert.Equal(new[] { "gpt-5-codex", "gpt-5" }, vm.LiveControls[0].Choices);
        Assert.Equal("gpt-5-codex", vm.LiveControls[0].SelectedValue);
        Assert.Equal("effort", vm.LiveControls[1].Key);
        Assert.Null(vm.LiveControls[1].SelectedValue);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task StartConfigured_LiveControls_ShowTheProviderChoiceLabels_WhileValuesRoundTripRaw()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        session.LiveOptions.Returns(
        [
            new SessionLiveOption("permissionMode", "Permissions", ["default", "plan"], "default")
            {
                ChoiceLabels = new Dictionary<string, string> { ["default"] = "Ask permissions", ["plan"] = "Plan mode" },
            },
        ]);
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        // Fase 4 step 1: the live-control dropdown reads the provider's friendly labels, while the value the driver
        // gets back on a switch stays the raw CLI value.
        var control = vm.LiveControls[0];
        Assert.Equal(new[] { "Ask permissions", "Plan mode" }, control.ChoiceItems.Select(choice => choice.Label));
        Assert.Equal(new[] { "default", "plan" }, control.ChoiceItems.Select(choice => choice.Value));

        await vm.DisposeAsync();
    }

    // AC-141: a session launched with no explicit model (Auto/default) opens the Model live-control with no
    // current value — the init event is the only place the CLI later states which one it actually picked.
    [Fact]
    public async Task SessionInitialized_WithModel_SeedsTheModelLiveControl_WithoutSwitchingTheDriver()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        session.LiveOptions.Returns(
        [
            new SessionLiveOption("model", "Model", ["opus", "sonnet"], null),
            new SessionLiveOption("effort", "Effort", ["low", "medium", "high"], "medium"),
        ]);
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));
        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        vm.Apply(new SessionInitialized { SessionId = "S1", Cwd = "", Tools = [], Model = "claude-sonnet-4-5-20250929" });

        Assert.Equal("claude-sonnet-4-5-20250929", vm.LiveControls[0].SelectedValue);
        // A pinned snapshot the suggestion list never offered still needs an item to show against.
        Assert.Contains("claude-sonnet-4-5-20250929", vm.LiveControls[0].ChoiceItems.Select(c => c.Value));
        await session.DidNotReceive().SetLiveOptionAsync("model", Arg.Any<string>(), Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SessionInitialized_WithModel_DoesNotOverwriteAnAlreadyChosenModel()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        session.LiveOptions.Returns(
        [
            new SessionLiveOption("model", "Model", ["opus", "sonnet"], "opus"),
        ]);
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));
        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        vm.Apply(new SessionInitialized { SessionId = "S1", Cwd = "", Tools = [], Model = "claude-sonnet-4-5-20250929" });

        Assert.Equal("opus", vm.LiveControls[0].SelectedValue);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task PickingALiveControlValue_SwitchesItOnTheDriver()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        session.LiveOptions.Returns(
        [
            new SessionLiveOption("effort", "Effort", ["low", "medium", "high"], null),
        ]);
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));
        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        // D4: picking a value in the panel forwards it to the running driver, which applies it to the next turn.
        vm.LiveControls[0].SelectedValue = "high";

        await session.Received(1).SetLiveOptionAsync("effort", "high", Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task CommitLiveModel_LiveSwitchesTheClaudeModel_ToAPinnedSnapshot()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));
        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        // The running-session model field is free text like the New-session dialog, so a specific snapshot can be
        // pinned live rather than only the three aliases — applied on commit (the view calls CommitLiveModel).
        vm.LiveModelText = "claude-sonnet-4-5-20250929";
        vm.CommitLiveModel();

        await session.Received(1).SetModelAsync("claude-sonnet-4-5-20250929", Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task CommitLiveModel_WithTheSameModel_FiresNoSwitch()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));
        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, new ModelOption("Sonnet", "sonnet"), SessionOptionCatalog.DefaultEffort);

        // A commit that changed nothing (the field still holds the launch model) must not fire a redundant switch.
        vm.CommitLiveModel();

        await session.DidNotReceive().SetModelAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    /// <summary>
    /// <see cref="SessionViewModel.CanPasteImages"/> (#64) follows <see cref="SessionCapabilities.SupportsVision"/>
    /// once the session has actually started — a Claude-CLI session reports it true since
    /// <see cref="SessionCapabilities.ClaudeCli"/> is the driver's real preset.
    /// </summary>
    [Fact]
    public async Task StartConfigured_ClaudeCliSession_ReportsCanPasteImagesTrue()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        session.Capabilities.Returns(SessionCapabilities.ClaudeCli);
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        Assert.True(vm.CanPasteImages);

        await vm.DisposeAsync();
    }

    /// <summary>
    /// A local (OpenAI-compatible) session's driver never sends pasted images (#64) — <see cref="SessionViewModel.CanPasteImages"/>
    /// reports false once such a session starts, mirroring how <see cref="SessionCapabilities.SupportsVision"/> stays
    /// false for that driver.
    /// </summary>
    [Fact]
    public async Task StartConfigured_LocalSession_ReportsCanPasteImagesFalse()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        session.Capabilities.Returns(new SessionCapabilities(
            SupportsTools: true, SupportsPermissions: false, SupportsLiveModelSwitch: false, SupportsPlanMode: false, SupportsThinking: false,
            SupportsVision: false));
        var localProfile = new SessionProfile(
            "ollama",
            new OllamaConfig("http://localhost:11434", "llama3.1"));
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));

        await vm.StartConfiguredAsync(
            localProfile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        Assert.False(vm.CanPasteImages);

        await vm.DisposeAsync();
    }

    /// <summary>
    /// The concrete gap #64 closes: before this, a pasted image queued as a pending attachment on any
    /// session regardless of provider, even though only Claude actually sent it — the operator saw a chip,
    /// sent the message, and the image vanished silently on a local/plugin session. Now
    /// <see cref="SessionViewModel.AddPastedImage"/> refuses the attachment when
    /// <see cref="SessionViewModel.CanPasteImages"/> is false and leaves a transcript notice instead.
    /// </summary>
    [Fact]
    public async Task AddPastedImage_WhenCanPasteImagesIsFalse_DoesNotQueueTheAttachment_AndLeavesATranscriptNotice()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        session.Capabilities.Returns(new SessionCapabilities(
            SupportsTools: true, SupportsPermissions: false, SupportsLiveModelSwitch: false, SupportsPlanMode: false, SupportsThinking: false,
            SupportsVision: false));
        var localProfile = new SessionProfile(
            "ollama",
            new OllamaConfig("http://localhost:11434", "llama3.1"));
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));
        await vm.StartConfiguredAsync(
            localProfile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        vm.AddPastedImage([1, 2, 3]);

        Assert.Empty(vm.PendingAttachments);
        Assert.Contains(vm.Transcript, entry => entry.Kind == TranscriptEntryKind.Error && entry.Text.Contains("does not support image input"));

        await vm.DisposeAsync();
    }

    /// <summary>
    /// <see cref="SessionPanelViewModel.ProviderBadge"/> lives on the shared base (#26) so the sidebar tile
    /// can bind to it regardless of session subtype; this proves a local provider's session sets it there.
    /// </summary>
    [Fact]
    public async Task StartConfigured_LocalSession_SetsTheBaseProviderBadge()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var localProfile = new SessionProfile(
            "ollama",
            new OllamaConfig("http://localhost:11434", "llama3.1"));
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));

        await vm.StartConfiguredAsync(
            localProfile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        Assert.Equal("Ollama", vm.ProviderBadge);

        await vm.DisposeAsync();
    }

    /// <summary>A Claude-CLI session needs no badge — it is the default provider and gets no sidebar/header pill.</summary>
    [Fact]
    public async Task StartConfigured_ClaudeCliSession_LeavesTheBaseProviderBadgeEmpty()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        Assert.Empty(vm.ProviderBadge);

        await vm.DisposeAsync();
    }

    [Fact]
    public void Apply_ThinkingDelta_OnDeveloper_AddsAThinkingRow_AndLeavesTheIndicatorUp()
    {
        var vm = NewVm(); // NewVm defaults to the Developer reading level
        vm.IsBusy = true; // a turn is in flight

        vm.Apply(new AssistantThinkingDelta { SessionId = "S1", BlockIndex = 0, Thinking = "Pondering..." });

        // AC-213 revises AC-144: reasoning deltas stream into a dimmed, collapsible Thinking row on Developer.
        Assert.Single(vm.Transcript, t => t.Kind == TranscriptEntryKind.Thinking && t.Text == "Pondering...");
        Assert.True(vm.Transcript.Single().IsRowVisible);
        // The pulsing indicator is separate from the row and stays lit while the turn runs.
        Assert.True(vm.ShowThinkingIndicator);
    }

    [Theory]
    [InlineData(ReadingLevel.Focus)]
    [InlineData(ReadingLevel.Simple)]
    public void Apply_ThinkingDelta_BelowDeveloper_AddsTheRowButKeepsItHidden(ReadingLevel level)
    {
        var vm = NewVm();
        vm.ReadingLevel = level;

        vm.Apply(new AssistantThinkingDelta { SessionId = "S1", BlockIndex = 0, Thinking = "Pondering..." });

        // The row is still added at every level, but Focus/Simple stay calm (AC-138): it renders hidden.
        var row = vm.Transcript.Single(t => t.Kind == TranscriptEntryKind.Thinking);
        Assert.False(row.IsRowVisible);
    }

    [Fact]
    public void Apply_ThinkingDeltas_SameBlock_StreamOntoOneRow()
    {
        var vm = NewVm();

        vm.Apply(new AssistantThinkingDelta { SessionId = "S1", BlockIndex = 0, Thinking = "Pon" });
        vm.Apply(new AssistantThinkingDelta { SessionId = "S1", BlockIndex = 0, Thinking = "dering..." });

        // Contiguous deltas of the same provider block append onto one row, like assistant prose.
        Assert.Single(vm.Transcript, t => t.Kind == TranscriptEntryKind.Thinking && t.Text == "Pondering...");
    }

    [Fact]
    public void Apply_ThinkingDeltas_DifferentBlocks_StartSeparateRows()
    {
        var vm = NewVm();

        // e.g. Codex's raw reasoning (block 0) and its summary (block 1) must not concatenate into one row.
        vm.Apply(new AssistantThinkingDelta { SessionId = "S1", BlockIndex = 0, Thinking = "raw" });
        vm.Apply(new AssistantThinkingDelta { SessionId = "S1", BlockIndex = 1, Thinking = "summary" });

        Assert.Equal(
            new[] { "raw", "summary" },
            vm.Transcript.Where(t => t.Kind == TranscriptEntryKind.Thinking).Select(t => t.Text));
    }

    [Fact]
    public void Apply_EmptyThinkingDelta_AddsNoRow()
    {
        var vm = NewVm();

        // A bare content_block_start carries empty thinking; it must not spawn an empty row.
        vm.Apply(new AssistantThinkingDelta { SessionId = "S1", BlockIndex = 0, Thinking = "" });

        Assert.Empty(vm.Transcript);
    }

    /// <summary>
    /// AC-532 round 2, and the exact defect measured in Raymond's transcript of 2026-08-01: the assistant says
    /// something and then goes back to work, and nothing marks that. The old flag treated the first text as "the
    /// wait is over" and was only ever re-armed by a <c>ToolResult</c>, so the composer stayed blank until the
    /// next tool call — three times in that one session, the longest 82.9 s. Streamed text ends a sentence, not
    /// a turn.
    /// </summary>
    [Fact]
    public void Apply_AssistantTextThenSilence_KeepsTheIndicatorUpUntilTheTurnEnds()
    {
        var vm = NewVm();
        vm.IsBusy = true;

        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "Let me check the tests." });

        Assert.True(vm.ShowThinkingIndicator);

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        Assert.False(vm.ShowThinkingIndicator);
    }

    [Fact]
    public void Apply_NonOutputEvent_LeavesTheThinkingIndicatorUp()
    {
        var vm = NewVm();
        vm.IsBusy = true;

        // A connect/status event is not the assistant answering, so the model is still "thinking".
        vm.Apply(new SessionInitialized { SessionId = "S1", Cwd = "", Tools = [] });

        Assert.True(vm.ShowThinkingIndicator);
    }

    [Fact]
    public void Apply_TextDeltaAfterThinking_AddsTheAssistantRowBeneathTheThinkingRow()
    {
        var vm = NewVm();
        vm.Apply(new AssistantThinkingDelta { SessionId = "S1", BlockIndex = 0, Thinking = "Pondering..." });

        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 1, Text = "Here you go." });

        // AC-213: the thinking row stays and the assistant text streams into its own row beneath it, in order.
        Assert.Equal(new[] { TranscriptEntryKind.Thinking, TranscriptEntryKind.AssistantText }, vm.Transcript.Select(t => t.Kind));
        Assert.Equal("Here you go.", vm.Transcript.Last().Text);
    }

    // AC-146: sub-agent activity nests under its parent Task tool-use row instead of flattening into the
    // top-level transcript, collapsed by default.
    [Fact]
    public void Apply_SubAgentActivity_NestsUnderItsParentToolUseRow_CollapsedByDefault()
    {
        var vm = NewVm();
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "task-1", ToolName = "Task", InputJson = "{}" });

        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "Looking into it…", ParentToolUseId = "task-1" });
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "sub-tool-1", ToolName = "Read", InputJson = "{}", ParentToolUseId = "task-1" });
        vm.Apply(new ToolResult { SessionId = "S1", ToolUseId = "sub-tool-1", Content = "file contents", IsError = false, ParentToolUseId = "task-1" });

        var anchor = Assert.Single(vm.Transcript);
        Assert.Equal(TranscriptEntryKind.ToolUse, anchor.Kind);
        Assert.False(anchor.IsSubAgentExpanded, "sub-agent activity is collapsed until the operator expands it");
        Assert.Equal(new[] { TranscriptEntryKind.AssistantText, TranscriptEntryKind.ToolUse }, anchor.SubAgentRows.Select(r => r.Kind));
        Assert.Equal("Looking into it…", anchor.SubAgentRows[0].Text);
        Assert.Equal("file contents", anchor.SubAgentRows[1].ResultText);
    }

    [Fact]
    public void Apply_SubAgentToolCallNeedingPermission_IsFoundNestedByToolUseId()
    {
        var vm = NewVm();
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "task-1", ToolName = "Task", InputJson = "{}" });
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "sub-tool-1", ToolName = "Bash", InputJson = "{}", ParentToolUseId = "task-1" });

        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "sub-tool-1", ToolName = "Bash", InputJson = "{}", ParentToolUseId = "task-1" });

        var anchor = Assert.Single(vm.Transcript);
        var nested = Assert.Single(anchor.SubAgentRows);
        Assert.True(nested.IsPendingPermission);
    }

    // AC-996: the needs-attention flag was set unconditionally while the consent card only exists where a row
    // does, so a permission whose tool-use event never arrived left the session waiting on the operator with
    // nothing on screen to answer. Neither observation in that ticket was this case, but nothing rules it out.
    [Fact]
    public void Apply_PermissionForAToolUseThatWasNeverSeen_StillGetsARowToApprove()
    {
        var vm = NewVm();

        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "ghost", ToolName = "Bash", InputJson = """{"command":"ls"}""" });

        var row = Assert.Single(vm.Transcript);
        Assert.Equal(TranscriptEntryKind.ToolUse, row.Kind);
        Assert.Equal("ghost", row.ToolUseId);
        Assert.Equal("Bash", row.ToolName);
        Assert.True(row.IsPendingPermission);
        Assert.True(vm.HasPendingPermission);
        Assert.Equal(SessionStatus.NeedsAttention, vm.SessionStatus);
    }

    // And the same for a sub-agent's call whose lane this pane never resolved: top-level, because a row nested
    // under an anchor that may be collapsed is precisely the row the operator cannot reach.
    [Fact]
    public void Apply_PermissionForAnUnresolvedSubAgentCall_GetsATopLevelRow()
    {
        var vm = NewVm();

        vm.Apply(new PermissionRequested
        {
            SessionId = "S1", ToolUseId = "sub-tool-1", ToolName = "Bash", InputJson = "{}", ParentToolUseId = "task-nobody-saw",
        });

        var row = Assert.Single(vm.Transcript);
        Assert.True(row.IsPendingPermission);
        Assert.Empty(row.SubAgentRows);
    }

    // AC-146 AC5: extract-last-assistant-text.js's own choice to exclude sidechain chatter from read-aloud must
    // not get inverted in the app's own read-aloud path — a sub-agent's text must never reach the operator's ears
    // as if the top-level reply said it.
    [Fact]
    public void SubAgentText_NeverReachesReadAloud_OnlyTheTopLevelReplyDoes()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var queue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)), voicePlaybackQueue: queue)
        {
            ReadResponsesAloud = true,
        };

        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "task-1", ToolName = "Task", InputJson = "{}" });
        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "sub-agent chatter that must stay hidden", ParentToolUseId = "task-1" });
        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 1, Text = "the actual reply" });
        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "the actual reply", IsError = false });

        queue.Received(1).Enqueue(
            Arg.Is<IReadOnlyList<string>>(sentences => sentences.All(s => !s.Contains("sub-agent chatter"))),
            Arg.Any<int>(), Arg.Any<string>());
        queue.Received(1).Enqueue(
            Arg.Is<IReadOnlyList<string>>(sentences => sentences.Any(s => s.Contains("the actual reply"))),
            Arg.Any<int>(), Arg.Any<string>());
    }

    // AC-146 AC5, defensive: an event naming a parent this pane never saw a top-level tool-use row for (a
    // dropped event, or a stray id — not expected with the current CLI/adapter, but not trusted blindly either)
    // must not be promoted into the top-level reply just because there is nowhere to nest it. It is still shown
    // (nothing vanishes silently) but stays out of read-aloud and the output-text signal, exactly like a
    // successfully-nested sub-agent chunk would.
    [Fact]
    public void SubAgentText_WithNoMatchingAnchor_StillNeverReachesReadAloud_ButIsStillShown()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var queue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)), voicePlaybackQueue: queue)
        {
            ReadResponsesAloud = true,
        };

        // No ToolUseRequested for "task-1" was ever applied — the anchor this parent id names does not exist.
        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "orphaned sub-agent chatter", ParentToolUseId = "task-1" });
        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 1, Text = "the actual reply" });
        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "the actual reply", IsError = false });

        Assert.Contains(vm.Transcript, row => row.Text == "orphaned sub-agent chatter");
        queue.Received(1).Enqueue(
            Arg.Is<IReadOnlyList<string>>(sentences => sentences.All(s => !s.Contains("orphaned sub-agent chatter"))),
            Arg.Any<int>(), Arg.Any<string>());
        queue.Received(1).Enqueue(
            Arg.Is<IReadOnlyList<string>>(sentences => sentences.Any(s => s.Contains("the actual reply"))),
            Arg.Any<int>(), Arg.Any<string>());
    }

    // The same fallback, but on a ToolResult naming an unresolved parent — must couple to (or fall back to a row
    // for) its tool-use row like any orphan, yet never raise the output-text/tool-activity signals a genuine
    // top-level result would.
    [Fact]
    public void OrphanedToolResult_CouplesAndShows_ButNeverRaisesOutputOrToolActivitySignals()
    {
        var vm = NewVm();
        var outputs = new List<string>();
        var toolActivity = new List<SessionToolActivity>();
        vm.OutputTextProduced += (_, text) => outputs.Add(text);
        vm.ToolActivityProduced += (_, activity) => toolActivity.Add(activity);

        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "sub-tool-1", ToolName = "Bash", InputJson = "{}", ParentToolUseId = "task-1" });
        vm.Apply(new ToolResult { SessionId = "S1", ToolUseId = "sub-tool-1", Content = "result text", IsError = false, ParentToolUseId = "task-1" });

        Assert.Contains(vm.Transcript, row => row.ResultText == "result text");
        Assert.DoesNotContain("result text", outputs);
        Assert.Empty(toolActivity);
    }

    [Fact]
    public void Apply_TextDeltaAfterThinking_ClosesTheThinkingRow_SoLaterThinkingStartsFresh()
    {
        var vm = NewVm();
        vm.Apply(new AssistantThinkingDelta { SessionId = "S1", BlockIndex = 0, Thinking = "first" });
        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 1, Text = "answer" });

        // Thinking resuming after visible prose (same block index) must not append back onto the closed row.
        vm.Apply(new AssistantThinkingDelta { SessionId = "S1", BlockIndex = 0, Thinking = "second" });

        Assert.Equal(
            new[] { "first", "second" },
            vm.Transcript.Where(t => t.Kind == TranscriptEntryKind.Thinking).Select(t => t.Text));
    }

    [Fact]
    public void Apply_ToolUseAfterThinking_AddsTheToolRowBeneathTheThinkingRow()
    {
        var vm = NewVm();
        vm.Apply(new AssistantThinkingDelta { SessionId = "S1", BlockIndex = 0, Thinking = "Pondering..." });

        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "toolu_1", ToolName = "Read", InputJson = "{}" });

        Assert.Equal(new[] { TranscriptEntryKind.Thinking, TranscriptEntryKind.ToolUse }, vm.Transcript.Select(t => t.Kind));
    }

    [Fact]
    public void Apply_FailedTurnCompletedAfterThinking_AddsTheTurnRowBeneathTheThinkingRow()
    {
        var vm = NewVm();
        vm.Apply(new AssistantThinkingDelta { SessionId = "S1", BlockIndex = 0, Thinking = "Pondering..." });

        // A failed turn is surfaced as a row; a successful one is not (T4), so use an error here.
        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "error", Result = "boom", IsError = true });

        Assert.Equal(new[] { TranscriptEntryKind.Thinking, TranscriptEntryKind.TurnCompleted }, vm.Transcript.Select(t => t.Kind));
    }

    [Theory]
    // User and tool-use rows carry their timestamp inline in their own header (AC-144), so the generic
    // top-of-row timestamp is suppressed for them; every other kind still shows it at the top.
    [InlineData(TranscriptEntryKind.UserText, false)]
    [InlineData(TranscriptEntryKind.ToolUse, false)]
    [InlineData(TranscriptEntryKind.AssistantText, true)]
    [InlineData(TranscriptEntryKind.ToolResult, true)]
    [InlineData(TranscriptEntryKind.Question, true)]
    [InlineData(TranscriptEntryKind.TurnCompleted, true)]
    [InlineData(TranscriptEntryKind.Error, true)]
    public void IsTopTimestampRow_IsFalseForUserAndToolUse_TrueForEveryOtherKind(TranscriptEntryKind kind, bool expected)
    {
        Assert.Equal(expected, new TranscriptEntryViewModel(kind, "x").IsTopTimestampRow);
    }

    [Fact]
    public void Apply_SuccessfulTurnCompleted_AddsNoTurnRow()
    {
        var vm = NewVm();

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        Assert.DoesNotContain(vm.Transcript, t => t.Kind == TranscriptEntryKind.TurnCompleted);
        Assert.Equal(SessionStatus.Done, vm.SessionStatus);
    }

    [Fact]
    public void Apply_TextDeltaWithNoPriorThinking_DoesNotThrow()
    {
        var vm = NewVm();

        var act = () => vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "hi" });

        act();
    }

    [Fact]
    public void Apply_PermissionRequested_SetsStatusToNeedsAttention()
    {
        var vm = NewVm();
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "toolu_1", ToolName = "Bash", InputJson = "{}" });

        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "toolu_1", ToolName = "Bash", InputJson = "{}" });

        Assert.Equal(SessionStatus.NeedsAttention, vm.SessionStatus);
    }

    [Fact]
    public async Task Apply_PermissionRequested_ForAPreApprovedTool_AutoAllows_WithoutPromptingOrNeedsAttention()
    {
        // AC-215: a self-driving embedded run pre-authorizes its own control tools, so a permission request for one is
        // auto-allowed here instead of raising a prompt the autonomous run (composer off) has no one to answer — the
        // stall that left a run stuck on its own autopilot_step_done.
        const string preApproved = "mcp__cockpit-autopilot-run__autopilot_step_done";
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        session.Capabilities.Returns(new SessionCapabilities(
            SupportsTools: true, SupportsPermissions: true, SupportsLiveModelSwitch: false, SupportsPlanMode: false, SupportsThinking: false));
        var profile = new SessionProfile("ollama", new OllamaConfig("http://localhost:11434", "llama3.1"));
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));
        await vm.StartConfiguredAsync(
            profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort,
            preApprovedTools: [preApproved]);

        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = preApproved, InputJson = "{}" });
        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "t1", ToolName = preApproved, InputJson = "{}" });

        var entry = vm.Transcript.Single(t => t.ToolUseId == "t1");
        Assert.False(entry.IsPendingPermission, "the pre-approved tool is auto-allowed, so no prompt is raised");
        Assert.Equal("Allowed", entry.PermissionDecision);
        Assert.NotEqual(SessionStatus.NeedsAttention, vm.SessionStatus);
        await session.Received(1).RespondToPermissionAsync("t1", true, Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Apply_PermissionRequested_ForAToolNotPreApproved_StillPrompts()
    {
        // The pre-approval is exact and narrow: a tool that is not on the list still raises the normal prompt, even in
        // a session that pre-approves others — file/shell/egress tools are never auto-allowed.
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        session.Capabilities.Returns(new SessionCapabilities(
            SupportsTools: true, SupportsPermissions: true, SupportsLiveModelSwitch: false, SupportsPlanMode: false, SupportsThinking: false));
        var profile = new SessionProfile("ollama", new OllamaConfig("http://localhost:11434", "llama3.1"));
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));
        await vm.StartConfiguredAsync(
            profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort,
            preApprovedTools: ["mcp__cockpit-autopilot-run__autopilot_step_done"]);

        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = "{}" });
        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = "{}" });

        Assert.True(vm.Transcript.Single(t => t.ToolUseId == "t1").IsPendingPermission);
        Assert.Equal(SessionStatus.NeedsAttention, vm.SessionStatus);
        await session.DidNotReceive().RespondToPermissionAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Apply_PermissionRequested_WhenPreApproveAllToolsIsSet_AutoAllowsEvenABashPrompt()
    {
        // "Worktree is the boundary" (Raymond 2026-07-23): an autonomous isolated run auto-allows every tool — not just
        // its own control tools — so its worker can run Bash/git/edits with no one to answer the prompt.
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        session.Capabilities.Returns(new SessionCapabilities(
            SupportsTools: true, SupportsPermissions: true, SupportsLiveModelSwitch: false, SupportsPlanMode: false, SupportsThinking: false));
        var profile = new SessionProfile("ollama", new OllamaConfig("http://localhost:11434", "llama3.1"));
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));
        await vm.StartConfiguredAsync(
            profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort,
            preApprovedTools: null, preApproveAllTools: true);

        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = "{}" });
        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = "{}" });

        var entry = vm.Transcript.Single(t => t.ToolUseId == "t1");
        Assert.False(entry.IsPendingPermission);
        Assert.Equal("Allowed", entry.PermissionDecision);
        Assert.NotEqual(SessionStatus.NeedsAttention, vm.SessionStatus);
        await session.Received(1).RespondToPermissionAsync("t1", true, Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public void Apply_SessionStatusChangedWithNeedsAction_SetsStatusToNeedsAttention()
    {
        var vm = NewVm();

        vm.Apply(new SessionStatusChanged { SessionId = "S1", NeedsAction = "answer_question" });

        Assert.Equal(SessionStatus.NeedsAttention, vm.SessionStatus);
    }

    [Fact]
    public void Apply_SessionStatusChangedWithoutNeedsAction_LeavesStatusIdle()
    {
        var vm = NewVm();

        vm.Apply(new SessionStatusChanged { SessionId = "S1", StatusCategory = "review_ready" });

        Assert.Equal(SessionStatus.Idle, vm.SessionStatus);
    }

    [Fact]
    public void Apply_ToolResult_CouplesToItsToolUseRowByToolUseId()
    {
        var vm = NewVm();
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "toolu_1", ToolName = "Edit", InputJson = "{}" });

        vm.Apply(new ToolResult { SessionId = "S1", ToolUseId = "toolu_1", Content = "done", IsError = false });

        var toolUse = vm.Transcript.Single(t => t.Kind == TranscriptEntryKind.ToolUse);
        Assert.True(toolUse.HasResult);
        Assert.Equal("done", toolUse.ResultText);
        Assert.DoesNotContain(vm.Transcript, t => t.Kind == TranscriptEntryKind.ToolResult);
    }

    [Fact]
    public void Apply_ToolResultWithNoMatchingToolUse_FallsBackToAStandaloneRow()
    {
        var vm = NewVm();

        vm.Apply(new ToolResult { SessionId = "S1", ToolUseId = "toolu_orphan", Content = "stray", IsError = false });

        Assert.Single(vm.Transcript, t => t.Kind == TranscriptEntryKind.ToolResult);
    }

    [Fact]
    public void Apply_ToolResultError_MarksTheCoupledResultAsAnError()
    {
        var vm = NewVm();
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "toolu_2", ToolName = "Bash", InputJson = "{}" });

        vm.Apply(new ToolResult { SessionId = "S1", ToolUseId = "toolu_2", Content = "boom", IsError = true });

        var toolUse = vm.Transcript.Single(t => t.Kind == TranscriptEntryKind.ToolUse);
        Assert.True(toolUse.IsResultError);
        Assert.True(toolUse.HasResult);
    }

    // AC-532: "Thinking…" clears the moment a tool call surfaces and only re-arms on its result — the widest
    // gap in a turn was left with no visible signal at all. These pin the replacement: a composer activity band
    // driven purely by ToolUseRequested/ToolResult (provider-neutral — every provider that reports tool calls
    // raises exactly these two), covering that gap without ever growing the composer or looking stuck.
    [Fact]
    public void Apply_ToolUseRequested_ShowsActiveToolActivity_UntilItsResultArrives()
    {
        var vm = NewVm();
        Assert.False(vm.HasActiveToolActivity);

        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = """{"command":"dotnet build"}""" });

        Assert.True(vm.HasActiveToolActivity);
        Assert.Equal("Bash  ·  dotnet build", vm.ActiveToolActivityLabel);

        vm.Apply(new ToolResult { SessionId = "S1", ToolUseId = "t1", Content = "ok", IsError = false });

        Assert.False(vm.HasActiveToolActivity);
        Assert.Equal(string.Empty, vm.ActiveToolActivityLabel);
    }

    [Fact]
    public void Apply_ToolUseRequested_ReplacesTheThinkingIndicator_NeverBothAtOnce()
    {
        var vm = NewVm();
        vm.IsBusy = true;

        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = "{}" });

        // The activity band takes the slot; the two never stack, so the composer keeps its height.
        Assert.False(vm.ShowThinkingIndicator);
        Assert.True(vm.HasActiveToolActivity);

        vm.Apply(new ToolResult { SessionId = "S1", ToolUseId = "t1", Content = "ok", IsError = false });

        // With the band empty again "Thinking…" is what shows — the turn is still running.
        Assert.True(vm.ShowThinkingIndicator);
    }

    /// <summary>
    /// AC-532 criterion 10: two tool calls back to back leave no gap, whether or not the assistant narrates
    /// between them. The narrated shape is the one that actually failed in the field — the plain back-to-back
    /// case was already covered by the tool result re-arming the old flag, which is why the defect survived
    /// round 1's suite.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_TwoToolCallsInARow_NeverLeaveTheComposerBlankBetweenThem(bool narratesBetween)
    {
        var vm = NewVm();
        vm.IsBusy = true;

        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = "{}" });
        vm.Apply(new ToolResult { SessionId = "S1", ToolUseId = "t1", Content = "ok", IsError = false });

        Assert.True(vm.ShowThinkingIndicator || vm.HasActiveToolActivity);

        if (narratesBetween)
        {
            vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "That worked. Now the other one." });
            Assert.True(vm.ShowThinkingIndicator || vm.HasActiveToolActivity);
        }

        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t2", ToolName = "PowerShell", InputJson = "{}" });

        Assert.True(vm.HasActiveToolActivity);
    }

    /// <summary>
    /// AC-532 criterion 6. A hung indicator is worse than none, so the three ways a turn can end each have to
    /// take both bands down — including the one where the driver dies without ever resulting its tool call.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Apply_ATurnThatEndsMidToolCall_LeavesNoIndicatorBehind(bool endsWithError)
    {
        var vm = NewVm();
        vm.IsBusy = true;
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = "{}" });

        Assert.True(vm.HasActiveToolActivity);

        if (endsWithError)
        {
            vm.Apply(new SessionError { SessionId = "S1", Message = "the driver died" });
        }
        else
        {
            vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });
        }

        Assert.False(vm.HasActiveToolActivity);
        Assert.False(vm.ShowThinkingIndicator);
    }

    [Fact]
    public void Apply_TwoParallelToolCalls_ShowsTheMostRecentlyRequestedOne_NeverStuckAfterEitherResolves()
    {
        var vm = NewVm();
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Read", InputJson = """{"file_path":"a.cs"}""" });
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t2", ToolName = "Read", InputJson = """{"file_path":"b.cs"}""" });

        Assert.True(vm.HasActiveToolActivity);
        Assert.Equal("Read  ·  b.cs", vm.ActiveToolActivityLabel);

        // The earlier call resolving first must not clear the band while the later one is still running. With a
        // turn in flight and a tool still outstanding this is the case ShowThinkingIndicator's guard exists for:
        // the activity band, not "Thinking…", is what the composer shows.
        vm.IsBusy = true;
        vm.Apply(new ToolResult { SessionId = "S1", ToolUseId = "t1", Content = "a", IsError = false });

        Assert.True(vm.HasActiveToolActivity);
        Assert.Equal("Read  ·  b.cs", vm.ActiveToolActivityLabel);
        Assert.False(vm.ShowThinkingIndicator);

        vm.Apply(new ToolResult { SessionId = "S1", ToolUseId = "t2", Content = "b", IsError = false });

        Assert.False(vm.HasActiveToolActivity);
    }

    [Fact]
    public void Apply_ToolInterruptedByAPermissionQuestion_StaysActiveUntilItsOwnResultArrives()
    {
        var vm = NewVm();
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = """{"command":"rm -rf x"}""" });

        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = """{"command":"rm -rf x"}""" });

        // A pending permission does not resolve the call — it is still outstanding, and the existing
        // pending-permission chip is a separate, stronger signal alongside the activity band, not a replacement.
        Assert.True(vm.HasActiveToolActivity);

        vm.Apply(new ToolResult { SessionId = "S1", ToolUseId = "t1", Content = "denied", IsError = true });

        Assert.False(vm.HasActiveToolActivity);
    }

    [Fact]
    public void Apply_TurnCompleted_ClearsOutstandingToolActivity_EvenWhenNoResultEverArrived()
    {
        // AC-532 AC6: an interrupted turn (Stop/interrupt) ends via TurnCompleted with no ToolResult for
        // whatever tool call was in flight — the activity band must not survive into a turn that is not running.
        var vm = NewVm();
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = """{"command":"long running"}""" });
        Assert.True(vm.HasActiveToolActivity);

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "error", Result = null, IsError = true });

        Assert.False(vm.HasActiveToolActivity);
    }

    [Fact]
    public void Apply_SessionError_ClearsOutstandingToolActivity_EvenWhenNoResultEverArrived()
    {
        // AC-532 AC6, the other failure path: the driver itself dies mid-call, so no ToolResult for the
        // outstanding tool ever arrives either — mirrors AC-276's handling of _backgroundTasks.
        var vm = NewVm();
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = """{"command":"long running"}""" });
        Assert.True(vm.HasActiveToolActivity);

        vm.Apply(new SessionError { SessionId = "S1", Message = "driver crashed" });

        Assert.False(vm.HasActiveToolActivity);
    }

    [Fact]
    public void Apply_SessionError_ClassifiesAnUntypedDriverFromItsMessageText()
    {
        // AC-720: a driver that has not been taught to set Kind (still Unknown) still gets a useful
        // presentation via the host's text heuristic.
        var vm = NewVm();

        vm.Apply(new SessionError { SessionId = "S1", Message = "429 Too Many Requests: rate limit exceeded" });

        var row = vm.Transcript.Single(t => t.Kind == TranscriptEntryKind.Error);
        Assert.Equal(SessionErrorKind.RateLimited, row.ErrorKind);
        Assert.True(row.IsTemporaryError);
    }

    [Fact]
    public void Apply_SessionError_TrustsADriverThatAlreadyClassifiedItself()
    {
        var vm = NewVm();

        vm.Apply(new SessionError { SessionId = "S1", Message = "some unrelated wording", Kind = SessionErrorKind.AuthRequired });

        var row = vm.Transcript.Single(t => t.Kind == TranscriptEntryKind.Error);
        Assert.Equal(SessionErrorKind.AuthRequired, row.ErrorKind);
        Assert.True(row.IsBlockingError);
    }

    [Fact]
    public void Apply_SessionError_UnclassifiableTextRendersInformational_NeverAGuessedSeverity()
    {
        var vm = NewVm();

        vm.Apply(new SessionError { SessionId = "S1", Message = "something odd happened" });

        var row = vm.Transcript.Single(t => t.Kind == TranscriptEntryKind.Error);
        Assert.Equal(SessionErrorKind.Unknown, row.ErrorKind);
        Assert.True(row.IsInformationalError);
    }

    [Fact]
    public void Apply_TurnCompleted_Error_ShowsTheProvidersReasonWhenTheEventCarriesOne()
    {
        // AC-720/AC-410: the subtype alone names nothing actionable; Errors carries the real reason.
        var vm = NewVm();

        vm.Apply(new TurnCompleted
        {
            SessionId = "S1",
            Subtype = "error_during_execution",
            Result = null,
            IsError = true,
            Errors = ["No conversation found."],
        });

        var row = vm.Transcript.Single(t => t.Kind == TranscriptEntryKind.TurnCompleted);
        Assert.Equal("Turn failed (error_during_execution): No conversation found.", row.Text);
        // AC-939: this reason does not match any classifier signal, so the subtype stays in the title and the
        // row renders informational rather than a guessed severity — same as an unclassified SessionError.
        Assert.Equal(SessionErrorKind.Unknown, row.ErrorKind);
        Assert.True(row.IsInformationalError);
    }

    [Fact]
    public void Apply_TurnCompleted_Error_FallsBackToTheSubtypeAloneWhenNoReasonIsGiven()
    {
        var vm = NewVm();

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "error", Result = null, IsError = true });

        var row = vm.Transcript.Single(t => t.Kind == TranscriptEntryKind.TurnCompleted);
        Assert.Equal("Turn failed (error)", row.Text);
        // AC-939: no reason to classify at all — stays Unknown/informational, never a guessed severity.
        Assert.Equal(SessionErrorKind.Unknown, row.ErrorKind);
        Assert.True(row.IsInformationalError);
    }

    [Fact]
    public void Apply_TurnCompleted_Error_WithARecognisedProviderOutage_DropsTheContradictorySubtypeAndRendersTemporary()
    {
        // AC-939: Claude reports an upstream 529 overload as `subtype: "success"` with `is_error: true` — the turn
        // ran to completion, the content is the error. The old title ("Turn failed (success)") was self-contradictory
        // and gave the operator no reason at all.
        var vm = NewVm();

        vm.Apply(new TurnCompleted
        {
            SessionId = "S1",
            Subtype = "success",
            Result = null,
            IsError = true,
            Errors = ["API Error: 529 Overloaded"],
        });

        var row = vm.Transcript.Single(t => t.Kind == TranscriptEntryKind.TurnCompleted);
        Assert.Equal("Turn failed: API Error: 529 Overloaded", row.Text);
        Assert.Equal(SessionErrorKind.ServiceUnavailable, row.ErrorKind);
        Assert.True(row.IsTemporaryError);
    }

    [Fact]
    public void Apply_SubAgentToolCall_NeverReplacesTheTopLevelActivityBand()
    {
        // AC-146: a sub-agent's own tool call nests under its Task row, already visible activity there — it must
        // not be promoted into the composer's top-level activity band, and must not clear the Task call's own.
        var vm = NewVm();
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "task-1", ToolName = "Task", InputJson = "{}" });
        Assert.Equal("Task", vm.ActiveToolActivityLabel);

        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "sub-1", ToolName = "Read", InputJson = """{"file_path":"x"}""", ParentToolUseId = "task-1" });

        Assert.Equal("Task", vm.ActiveToolActivityLabel);

        vm.Apply(new ToolResult { SessionId = "S1", ToolUseId = "sub-1", Content = "x", IsError = false, ParentToolUseId = "task-1" });

        // The sub-agent's own result must not clear the still-outstanding top-level Task call either.
        Assert.True(vm.HasActiveToolActivity);
        Assert.Equal("Task", vm.ActiveToolActivityLabel);
    }

    // AC-532 permission-wait state: a tool call blocked on a permission prompt is not "running" — it is waiting on
    // the operator, which is a different fact and reads misleadingly under a still-climbing "running m:ss".
    [Fact]
    public void Apply_PermissionRequested_ForTheOutstandingCall_SwitchesTheBandToWaitingForPermission()
    {
        var vm = NewVm();
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = """{"command":"rm -rf x"}""" });
        Assert.StartsWith("running ", vm.ActiveToolActivityAgeText, StringComparison.Ordinal);

        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = """{"command":"rm -rf x"}""" });

        Assert.True(vm.HasActiveToolActivity);
        Assert.Equal("Bash  ·  rm -rf x", vm.ActiveToolActivityLabel);
        Assert.Equal("waiting for permission", vm.ActiveToolActivityAgeText);
    }

    [Fact]
    public async Task AllowTool_WhileItWasTheReasonTheBandSaidWaitingForPermission_RevertsToRunning()
    {
        var (vm, _) = await StartedVm();
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = """{"command":"rm -rf x"}""" });
        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = """{"command":"rm -rf x"}""" });
        Assert.Equal("waiting for permission", vm.ActiveToolActivityAgeText);
        var entry = vm.Transcript.Single(t => t.ToolUseId == "t1");

        await vm.AllowToolCommand.ExecuteAsync(entry);

        // Allowed, but the call itself has not resulted yet — the band reverts to the normal running text
        // rather than disappearing, and must not still say "waiting for permission" for a decision already made.
        Assert.True(vm.HasActiveToolActivity);
        Assert.StartsWith("running ", vm.ActiveToolActivityAgeText, StringComparison.Ordinal);

        vm.Apply(new ToolResult { SessionId = "S1", ToolUseId = "t1", Content = "ok", IsError = false });
        Assert.False(vm.HasActiveToolActivity);
    }

    [Fact]
    public async Task DenyTool_WhileItWasTheReasonTheBandSaidWaitingForPermission_RevertsToRunningUntilItsResultArrives()
    {
        var (vm, _) = await StartedVm();
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = """{"command":"rm -rf x"}""" });
        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = """{"command":"rm -rf x"}""" });
        Assert.Equal("waiting for permission", vm.ActiveToolActivityAgeText);
        var entry = vm.Transcript.Single(t => t.ToolUseId == "t1");

        await vm.DenyToolCommand.ExecuteAsync(entry);

        Assert.True(vm.HasActiveToolActivity);
        Assert.StartsWith("running ", vm.ActiveToolActivityAgeText, StringComparison.Ordinal);

        // A denial is reported back as a tool result (see Apply_ToolInterruptedByAPermissionQuestion... above),
        // which is what finally clears the band.
        vm.Apply(new ToolResult { SessionId = "S1", ToolUseId = "t1", Content = "denied", IsError = true });
        Assert.False(vm.HasActiveToolActivity);
    }

    [Fact]
    public void Apply_TwoOutstandingCalls_OneWaitingOnPermission_SurfacesTheWaitingOneEvenWhenItIsNotTheMostRecent()
    {
        // t1 (Bash) is requested first and immediately pauses on a permission prompt; t2 (Read) is requested
        // second and needs no approval, so on the pre-existing "most recently requested" rule alone t2 would be
        // what the band shows — silently hiding the exact thing this ticket exists to surface.
        var vm = NewVm();
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = """{"command":"rm -rf x"}""" });
        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = """{"command":"rm -rf x"}""" });
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t2", ToolName = "Read", InputJson = """{"file_path":"b.cs"}""" });

        Assert.Equal("Bash  ·  rm -rf x", vm.ActiveToolActivityLabel);
        Assert.Equal("waiting for permission", vm.ActiveToolActivityAgeText);

        // t2 finishing first must not disturb the band still waiting on t1's permission decision.
        vm.Apply(new ToolResult { SessionId = "S1", ToolUseId = "t2", Content = "b", IsError = false });
        Assert.Equal("Bash  ·  rm -rf x", vm.ActiveToolActivityLabel);
        Assert.Equal("waiting for permission", vm.ActiveToolActivityAgeText);

        vm.Apply(new ToolResult { SessionId = "S1", ToolUseId = "t1", Content = "denied", IsError = true });
        Assert.False(vm.HasActiveToolActivity);
    }

    [Fact]
    public void Apply_SessionError_WhileATopLevelCallWasWaitingOnPermission_ClearsTheBandRatherThanLeavingItStuck()
    {
        // AC-532 AC6/pitfall: a permission prompt the SDK route has no safety-timeout for, followed by the
        // session dying before the operator ever answers, must not leave the composer reading "waiting for
        // permission" forever.
        var vm = NewVm();
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = """{"command":"rm -rf x"}""" });
        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = """{"command":"rm -rf x"}""" });
        Assert.Equal("waiting for permission", vm.ActiveToolActivityAgeText);

        vm.Apply(new SessionError { SessionId = "S1", Message = "driver crashed" });

        Assert.False(vm.HasActiveToolActivity);
        Assert.Equal(string.Empty, vm.ActiveToolActivityAgeText);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(12, "0:12")]
    [InlineData(65, "1:05")]
    [InlineData(-5, "0:00")] // a clock that moved back reads as "just started", not a negative duration
    public void FormatElapsed_RendersMinutesColonSeconds(int seconds, string expected)
    {
        Assert.Equal(expected, SessionViewModel._FormatElapsed(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Apply_TextThatStreamsAfterAToolCall_StartsANewRowBeneathTheTool_NotMergedAbove()
    {
        var vm = NewVm();
        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "Let me check. " });
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "toolu_1", ToolName = "Read", InputJson = "{}" });
        vm.Apply(new ToolResult { SessionId = "S1", ToolUseId = "toolu_1", Content = "file contents", IsError = false });
        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 1, Text = "Here is the summary." });

        var assistantRows = vm.Transcript.Where(t => t.Kind == TranscriptEntryKind.AssistantText).ToList();
        Assert.Equal(2, System.Linq.Enumerable.Count(assistantRows));
        Assert.Equal("Let me check. ", assistantRows[0].Text);
        Assert.Equal("Here is the summary.", assistantRows[1].Text);

        var toolIndex = vm.Transcript.IndexOf(vm.Transcript.Single(t => t.Kind == TranscriptEntryKind.ToolUse));
        var postToolIndex = vm.Transcript.IndexOf(assistantRows[1]);
        Assert.True(postToolIndex > toolIndex, "text that streamed after the tool call must render below it, in order");
    }

    [Fact]
    public void ToolHeader_CompactsToToolNameAndAShortHint()
    {
        var vm = NewVm();
        vm.Apply(new ToolUseRequested
        {
            SessionId = "S1",
            ToolUseId = "toolu_3",
            ToolName = "Bash",
            InputJson = """{"command":"dotnet build"}""",
        });

        var toolUse = vm.Transcript.Single(t => t.Kind == TranscriptEntryKind.ToolUse);
        Assert.Contains("Bash", toolUse.ToolHeader);
        Assert.Contains("dotnet build", toolUse.ToolHeader);
    }

    [Fact]
    public void ResultIsCodeLike_ForJsonResult_IsTrueAndPrettyPrinted()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "Tool: X");
        entry.SetResult("""{"a":1,"b":[2,3]}""", isError: false);

        Assert.True(entry.ResultIsCodeLike);
        Assert.Contains("\n", entry.ResultDisplayText);
    }

    [Fact]
    public void ResultIsCodeLike_ForShortPlainResult_IsFalse()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "Tool: X");
        entry.SetResult("done", isError: false);

        Assert.False(entry.ResultIsCodeLike);
        Assert.Equal("done", entry.ResultDisplayText);
    }

    [Fact]
    public void AssistantTextRow_RendersAsMarkdown()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "Some **bold** prose.");

        Assert.True(entry.IsAssistantMarkdown);
        Assert.False(entry.IsPlainNonMarkdown);
    }

    [Fact]
    public void UserRow_RendersAsItsOwnBubbleNotMarkdownNorPlain()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "build the project");

        Assert.True(entry.IsUserRow);
        Assert.False(entry.IsAssistantMarkdown);
        Assert.False(entry.IsPlainNonMarkdown);
    }

    [Fact]
    public async Task SendingAMessage_EchoesItAsAUserRowWithoutAPrefix()
    {
        var (vm, _) = await StartedVm();
        vm.InputText = "hello there";

        await vm.SendCommand.ExecuteAsync(null);

        var echo = Assert.Single(vm.Transcript, t => t.Kind == TranscriptEntryKind.UserText);
        Assert.Equal("hello there", echo.Text);
        Assert.True(echo.IsUserRow);
        await vm.DisposeAsync();
    }

    /// <summary>
    /// AC-693: the operator was shown "Send failed: The pipe is being closed." — the raw IOException from writing
    /// into the stdin of a process that had already died. Both readings of that death are covered: the exception
    /// itself, and a runtime the pump has by now noticed is gone. A genuine send error keeps its own words.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ABrokenPipe_IsReportedAsAStoppedSession_NotAsItsOwnException(bool runtimeIsRunning)
    {
        var message = SessionViewModel.SendFailureMessage(
            new IOException("The pipe is being closed."), runtimeIsRunning);

        Assert.DoesNotContain("pipe", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("This session's process has stopped, so the message was not sent.", message);
    }

    [Fact]
    public void ASendThatFailsOnARuntimeThatIsStillRunning_KeepsTheProvidersOwnMessage() =>
        Assert.Equal(
            "Send failed: boom",
            SessionViewModel.SendFailureMessage(new InvalidOperationException("boom"), runtimeIsRunning: true));

    [Fact]
    public void ASendThatFailsAfterTheRuntimeStopped_ReportsTheStoppedSession()
    {
        // The wrapped case: the driver refused the write with something other than an IOException, and by the time
        // the failure lands the pump has already seen stdout end.
        var message = SessionViewModel.SendFailureMessage(
            new InvalidOperationException("The session has not been started."), runtimeIsRunning: false);

        Assert.Equal("This session's process has stopped, so the message was not sent.", message);
    }

    // AC-935: the wire text is the only thing that changes with a reply — the transcript row stays plain, and
    // an ordinary message (no target) costs no extra tokens.
    [Fact]
    public void BuildOutgoingText_WithNoReplyTarget_IsUnchanged() =>
        Assert.Equal("looks fine to me", SessionViewModel.BuildOutgoingText("looks fine to me", replyTo: null));

    [Fact]
    public void BuildOutgoingText_WithAReplyTarget_PrefixesWithACitationOfTheTarget()
    {
        var target = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "please check the build output");

        var outgoing = SessionViewModel.BuildOutgoingText("looks fine to me", target);

        Assert.Equal("[reply to \"please check the build output\"]: looks fine to me", outgoing);
    }

    // AC-935: sending a reply prefixes only the wire text — the echoed row stays plain, the target it answered
    // is marked answered, and the composer's own pending target clears once it has been used.
    [Fact]
    public async Task SendingAReply_PrefixesTheWireTextOnly_AndMarksTheTargetAnswered()
    {
        var (vm, session) = await StartedVm();
        var target = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "please check the build output");
        vm.PendingReplyTo = target;
        vm.InputText = "looks fine to me";

        await vm.SendCommand.ExecuteAsync(null);

        await session.Received(1).SendUserMessageAsync(
            "[reply to \"please check the build output\"]: looks fine to me",
            Arg.Any<IReadOnlyList<ImageAttachment>>(),
            Arg.Any<CancellationToken>());

        var echo = Assert.Single(vm.Transcript, t => t.Kind == TranscriptEntryKind.UserText);
        Assert.Equal("looks fine to me", echo.Text);
        Assert.Same(target, echo.ReplyTo);
        Assert.Same(echo, target.LatestReply);
        Assert.True(target.HasReplies);
        Assert.Null(vm.PendingReplyTo);
        await vm.DisposeAsync();
    }

    // AC-935 criterion 7: a reply typed while a turn is in flight goes onto the queue like any other message —
    // its target must ride along, or it is lost the moment the composer's own pending target is cleared.
    [Fact]
    public async Task SendingAReplyWhileATurnIsInFlight_QueuesItWithItsTarget()
    {
        var (vm, _) = await StartedVm();
        vm.InputText = "first";
        await vm.SendCommand.ExecuteAsync(null); // turn now in flight

        var target = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "question");
        vm.PendingReplyTo = target;
        vm.InputText = "answer";
        await vm.SendCommand.ExecuteAsync(null);

        var queued = Assert.Single(vm.QueuedMessages);
        Assert.Same(target, queued.ReplyTo);
        Assert.Null(vm.PendingReplyTo);
        await vm.DisposeAsync();
    }

    // AC-935 §6.2: combine mode merges several queued messages into one turn — a single prefix over the whole
    // blob would misattribute every message but the first, so each sub-message gets its own.
    [Fact]
    public async Task TurnCompleted_WithCombineOn_GivesEachQueuedReplyItsOwnPrefix()
    {
        var (vm, session) = await StartedVm();
        vm.CombineQueuedMessages = true;
        vm.InputText = "first";
        await vm.SendCommand.ExecuteAsync(null); // dispatched immediately, turn now in flight

        var targetA = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "question A");
        var targetB = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "question B");
        vm.QueuedMessages.Add(new QueuedMessageViewModel("answer A", [], targetA, m => vm.QueuedMessages.Remove(m)));
        vm.QueuedMessages.Add(new QueuedMessageViewModel("answer B", [], targetB, m => vm.QueuedMessages.Remove(m)));

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        await session.Received(1).SendUserMessageAsync(
            "[reply to \"question A\"]: answer A\n\n[reply to \"question B\"]: answer B",
            Arg.Any<IReadOnlyList<ImageAttachment>>(),
            Arg.Any<CancellationToken>());
        await vm.DisposeAsync();
    }

    [Fact]
    public void ErrorRow_RendersItsOwnCardInsteadOfThePlainPath()
    {
        // AC-720: an error row used to fall through to the same plain-text branch as a question/turn-result
        // row (IsPlainNonMarkdown); it now gets its own severity-coloured card (IsErrorRow).
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.Error, "Send failed: boom");

        Assert.False(entry.IsAssistantMarkdown);
        Assert.False(entry.IsPlainNonMarkdown);
        Assert.True(entry.IsErrorRow);
    }

    /// <summary>
    /// AC-410: the SDK route for a restored pane whose resume failed — mirrors what the real CLI does for an
    /// unresolvable --resume id (verified 2026-07-29): an error_during_execution result with no Result and the
    /// reason only in errors[]. Asserted with xunit's own Assert (AC-372) rather than this file's FluentAssertions,
    /// per the newer test-file convention.
    /// </summary>
    [Fact]
    public async Task RestoredPane_FirstTurnFailsWithNoResult_DegradesTheOfferWithTheProvidersReason()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));
        var pane = new Cockpit.Core.Workspaces.WorkspacePane("p1", Cockpit.Core.Workspaces.PaneKind.AiSession) { ProfileId = "default" };
        // Set before StartConfiguredAsync, which snapshots it — CockpitViewModel would already have cleared
        // RestoreOffer itself by the time a real turn completes (see _restoredOfferSnapshot's own doc).
        vm.RestoreOffer = new Cockpit.App.Services.SessionRestorePlan(pane, Profile, Cockpit.App.Services.SessionRestoreAvailability.Known, string.Empty);

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        vm.Apply(new TurnCompleted
        {
            SessionId = "S1",
            Subtype = "error_during_execution",
            Result = null,
            IsError = true,
            Errors = ["No conversation found with session ID: 00000000-dead-beef-0000-000000000000"],
        });

        Assert.True(vm.HasRestoreOffer, "a failed resume must come back as an offer, not a silently dead session");
        Assert.False(vm.CanResumeConversation, "the conversation it just failed to resume must not be offered again as if nothing happened");
        Assert.Contains("No conversation found with session ID", vm.RestoreDegradedReason);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SendAsync_WhileTurnInFlight_SetsStatusToBusy()
    {
        var (vm, _) = await StartedVm();
        vm.InputText = "hello";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(SessionStatus.Busy, vm.SessionStatus);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SendAsync_BeforeStart_ShowsAFriendlyErrorAndKeepsTheText()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session))) { InputText = "hello" };

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal("hello", vm.InputText);
        Assert.Contains("not started", Assert.Single(vm.Transcript, t => t.Kind == TranscriptEntryKind.Error).Text);
        await session.DidNotReceive().SendUserMessageAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ImageAttachment>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WhileBusy_QueuesTheMessageInsteadOfSending()
    {
        var (vm, session) = await StartedVm();
        vm.InputText = "first";

        await vm.SendCommand.ExecuteAsync(null); // first send goes out immediately, turn now in flight
        vm.InputText = "second";
        await vm.SendCommand.ExecuteAsync(null); // second lands in the queue while busy

        Assert.Equal(new[] { "second" }, vm.QueuedMessages.Select(m => m.Text));
        Assert.Empty(vm.InputText);
        await session.Received(1).SendUserMessageAsync("first", Arg.Any<IReadOnlyList<ImageAttachment>>(), Arg.Any<CancellationToken>());
        await session.DidNotReceive().SendUserMessageAsync("second", Arg.Any<IReadOnlyList<ImageAttachment>>(), Arg.Any<CancellationToken>());
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task TurnCompleted_DispatchesTheNextQueuedMessage()
    {
        var (vm, session) = await StartedVm();
        vm.InputText = "first";
        await vm.SendCommand.ExecuteAsync(null);
        vm.InputText = "second";
        await vm.SendCommand.ExecuteAsync(null);

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        Assert.Empty(vm.QueuedMessages);
        await session.Received(1).SendUserMessageAsync("second", Arg.Any<IReadOnlyList<ImageAttachment>>(), Arg.Any<CancellationToken>());
        Assert.Equal(SessionStatus.Busy, vm.SessionStatus);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task TurnCompleted_WithCombineOn_DispatchesAllQueuedMessagesAsOneTurn()
    {
        var (vm, session) = await StartedVm();
        vm.CombineQueuedMessages = true;
        vm.InputText = "first";
        await vm.SendCommand.ExecuteAsync(null); // dispatched immediately, turn now in flight
        vm.InputText = "second";
        await vm.SendCommand.ExecuteAsync(null); // queued
        vm.InputText = "third";
        await vm.SendCommand.ExecuteAsync(null); // queued

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        // Both queued messages leave together as a single follow-up turn (AC-145), joined by a blank line —
        // not "second" now and "third" after the next turn.
        Assert.Empty(vm.QueuedMessages);
        await session.Received(1).SendUserMessageAsync("second\n\nthird", Arg.Any<IReadOnlyList<ImageAttachment>>(), Arg.Any<CancellationToken>());
        await session.DidNotReceive().SendUserMessageAsync("second", Arg.Any<IReadOnlyList<ImageAttachment>>(), Arg.Any<CancellationToken>());
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task TurnCompleted_WithCombineOn_AndASingleQueuedMessage_DispatchesItAsIs()
    {
        var (vm, session) = await StartedVm();
        vm.CombineQueuedMessages = true;
        vm.InputText = "first";
        await vm.SendCommand.ExecuteAsync(null);
        vm.InputText = "second";
        await vm.SendCommand.ExecuteAsync(null); // the only queued message

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        // A single queued message is dispatched verbatim. (Joining one element is identity, so the output can't
        // by itself prove which path ran — this just asserts the plain result and that nothing is left queued.)
        Assert.Empty(vm.QueuedMessages);
        await session.Received(1).SendUserMessageAsync("second", Arg.Any<IReadOnlyList<ImageAttachment>>(), Arg.Any<CancellationToken>());
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task TurnCompleted_WithCombineOn_MergesImagesFromAllQueuedMessagesInOrder()
    {
        var (vm, session) = await StartedVm();
        vm.CombineQueuedMessages = true;
        vm.InputText = "first";
        await vm.SendCommand.ExecuteAsync(null); // dispatched immediately, turn now in flight

        // Queue two messages carrying images — one with text, one image-only — directly on the send queue.
        var imageA = ImageAttachment.FromBytes([1], "image/png");
        var imageB = ImageAttachment.FromBytes([2], "image/png");
        vm.QueuedMessages.Add(new QueuedMessageViewModel("look at these", [imageA], replyTo: null, m => vm.QueuedMessages.Remove(m)));
        vm.QueuedMessages.Add(new QueuedMessageViewModel("", [imageB], replyTo: null, m => vm.QueuedMessages.Remove(m)));

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        // The empty text is dropped from the joined prose; both images carry over in queue order.
        await session.Received(1).SendUserMessageAsync(
            "look at these",
            Arg.Is<IReadOnlyList<ImageAttachment>>(images => images.Count == 2 && images[0] == imageA && images[1] == imageB),
            Arg.Any<CancellationToken>());
        Assert.Empty(vm.QueuedMessages);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task TurnCompleted_WithCombineOn_AllImageOnly_SendsEmptyTextWithEveryImage()
    {
        var (vm, session) = await StartedVm();
        vm.CombineQueuedMessages = true;
        vm.InputText = "first";
        await vm.SendCommand.ExecuteAsync(null);

        var imageA = ImageAttachment.FromBytes([1], "image/png");
        var imageB = ImageAttachment.FromBytes([2], "image/png");
        vm.QueuedMessages.Add(new QueuedMessageViewModel("", [imageA], replyTo: null, m => vm.QueuedMessages.Remove(m)));
        vm.QueuedMessages.Add(new QueuedMessageViewModel("   ", [imageB], replyTo: null, m => vm.QueuedMessages.Remove(m)));

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        // Every queued chip was image-only, so the combined text is empty but all images still go out together.
        await session.Received(1).SendUserMessageAsync(
            "",
            Arg.Is<IReadOnlyList<ImageAttachment>>(images => images.Count == 2),
            Arg.Any<CancellationToken>());
        Assert.Empty(vm.QueuedMessages);
        await vm.DisposeAsync();
    }

    // AC-778: the bytes ride along on the echoed transcript row itself, not just as a suffix baked into its text.
    // Dispatched via the queue (rather than `AddPastedImage`, which decodes a `Bitmap` and needs a real Avalonia
    // platform this host does not initialize) — the same `_DispatchMessageAsync` funnel either way.
    [Fact]
    public async Task QueuedSend_WithAnImage_AttachesItToTheEchoedTranscriptRow()
    {
        var (vm, _) = await StartedVm();
        vm.CombineQueuedMessages = true;
        vm.InputText = "first";
        await vm.SendCommand.ExecuteAsync(null); // dispatched immediately, turn now in flight

        var image = ImageAttachment.FromBytes([1, 2, 3], "image/png");
        vm.QueuedMessages.Add(new QueuedMessageViewModel("look at this", [image], replyTo: null, m => vm.QueuedMessages.Remove(m)));

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        var echoed = vm.Transcript.Last(entry => entry.Kind == TranscriptEntryKind.UserText);
        Assert.True(echoed.HasImages);
        Assert.Equal("image/png", Assert.Single(echoed.Images!).MediaType);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Send_WithoutAnImage_LeavesTheEchoedTranscriptRowWithoutImages()
    {
        var (vm, _) = await StartedVm();
        vm.InputText = "just text";

        await vm.SendCommand.ExecuteAsync(null);

        var echoed = Assert.Single(vm.Transcript, entry => entry.Kind == TranscriptEntryKind.UserText);
        Assert.False(echoed.HasImages);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task RecallLastQueuedMessage_PullsTheNewestQueuedMessageBackIntoTheInput()
    {
        var (vm, _) = await StartedVm();
        vm.InputText = "first";
        await vm.SendCommand.ExecuteAsync(null);
        vm.InputText = "queued one";
        await vm.SendCommand.ExecuteAsync(null);
        vm.InputText = "queued two";
        await vm.SendCommand.ExecuteAsync(null);

        var recalled = vm.RecallLastQueuedMessage();

        Assert.True(recalled);
        Assert.Equal("queued two", vm.InputText);
        Assert.Equal(new[] { "queued one" }, vm.QueuedMessages.Select(m => m.Text));
        await vm.DisposeAsync();
    }

    [Fact]
    public void RecallLastQueuedMessage_WithAnEmptyQueue_ReturnsFalseAndLeavesInputUntouched()
    {
        var vm = NewVm();

        Assert.False(vm.RecallLastQueuedMessage());
        Assert.Empty(vm.InputText);
    }

    [Fact]
    public async Task RemovingAQueuedChip_CancelsThatMessage()
    {
        var (vm, session) = await StartedVm();
        vm.InputText = "first";
        await vm.SendCommand.ExecuteAsync(null);
        vm.InputText = "cancel me";
        await vm.SendCommand.ExecuteAsync(null);

        vm.QueuedMessages.Single().RemoveCommand.Execute(null);

        Assert.Empty(vm.QueuedMessages);
        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });
        await session.DidNotReceive().SendUserMessageAsync("cancel me", Arg.Any<IReadOnlyList<ImageAttachment>>(), Arg.Any<CancellationToken>());
        await vm.DisposeAsync();
    }

    [Fact]
    public void CanSend_IsFalseWithoutContentAndTrueOnceTextIsTyped()
    {
        var vm = NewVm();

        Assert.False(vm.CanSend);

        vm.InputText = "hi";

        Assert.True(vm.CanSend);
    }

    [Fact]
    public void TimestampText_IsTheArrivalTimeAsHoursAndMinutes()
    {
        var entry = new TranscriptEntryViewModel(
            TranscriptEntryKind.AssistantText, "hi", new DateTimeOffset(2026, 7, 6, 14, 7, 0, TimeSpan.Zero));

        Assert.Equal("14:07", entry.TimestampText);
    }

    [Fact]
    public async Task ExitMessage_WithAutoCloseOn_IsStillSentAndClosesTheSessionWhenTheTurnCompletes()
    {
        var (vm, session) = await StartedVm();
        vm.AutoCloseOnExit = true;
        var closeRequested = false;
        vm.CloseRequested += (_, _) => closeRequested = true;
        vm.InputText = "exit";
        await vm.SendCommand.ExecuteAsync(null);

        await session.Received(1).SendUserMessageAsync("exit", Arg.Any<IReadOnlyList<ImageAttachment>>(), Arg.Any<CancellationToken>());
        Assert.False(closeRequested); // not until the turn finishes

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "bye", IsError = false });

        Assert.True(closeRequested);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task ExitMessage_WithAutoCloseOff_DoesNotCloseTheSession()
    {
        var (vm, _) = await StartedVm();
        vm.AutoCloseOnExit = false;
        var closeRequested = false;
        vm.CloseRequested += (_, _) => closeRequested = true;
        vm.InputText = "exit";
        await vm.SendCommand.ExecuteAsync(null);

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        Assert.False(closeRequested);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task NonExitMessage_WithAutoCloseOn_DoesNotCloseTheSession()
    {
        var (vm, _) = await StartedVm();
        vm.AutoCloseOnExit = true;
        var closeRequested = false;
        vm.CloseRequested += (_, _) => closeRequested = true;
        vm.InputText = "hello";
        await vm.SendCommand.ExecuteAsync(null);

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        Assert.False(closeRequested);
        await vm.DisposeAsync();
    }

    [Fact]
    public void Apply_TurnCompletedAfterPermissionRequest_PriorityGoesToNeedsAttention()
    {
        var vm = NewVm();
        vm.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "toolu_1", ToolName = "Bash", InputJson = "{}" });
        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "toolu_1", ToolName = "Bash", InputJson = "{}" });

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        Assert.Equal(SessionStatus.NeedsAttention, vm.SessionStatus);
    }

    [Fact]
    public void Efforts_MapEachLevelToItsThinkingBudget()
    {
        var vm = NewVm();

        Assert.Equal(
            new[]
            {
                ("low", 4_000),
                ("medium", 12_000),
                ("high", 24_000),
                ("xhigh", 48_000),
                ("max", 64_000),
            },
            vm.Efforts.Select(e => (e.Value, e.MaxThinkingTokens)));
    }

    [Fact]
    public void PermissionModes_WhenNotLocked_OfferOnlyTheThreeLiveModes()
    {
        var vm = NewVm();

        Assert.Equal(new[] { "default", "acceptEdits", "plan" }, vm.PermissionModes.Select(mode => mode.Value));
    }

    [Fact]
    public async Task SelectedEffortChanged_WhileLive_SendsTheNewBudget()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));
        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);
        session.ClearReceivedCalls();

        vm.SelectedEffort = new EffortOption("Max", "max", 64_000);

        await session.Received(1).SetMaxThinkingTokensAsync(64_000, Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public void SelectedEffortChanged_BeforeStart_DoesNotTouchTheSession()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));

        vm.SelectedEffort = new EffortOption("High", "high", 24_000);

        session.DidNotReceive().SetMaxThinkingTokensAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AllowAlwaysExactTool_ResolvesTheSessionWithAnExactAlwaysRule()
    {
        var (vm, session) = await StartedVm();
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "Tool: Bash")
        {
            ToolUseId = "toolu_1",
            ToolName = "Bash",
            InputJson = """{"command":"ls"}""",
            IsPendingPermission = true,
        };

        await vm.AllowAlwaysExactToolCommand.ExecuteAsync(entry);

        await session.Received(1).AllowPermissionAlwaysAsync(
            "toolu_1", "Bash", """{"command":"ls"}""", PermissionRuleScope.Exact, Arg.Any<CancellationToken>());
        Assert.False(entry.IsPendingPermission);
        Assert.False(string.IsNullOrEmpty(entry.PermissionDecision));
    }

    [Fact]
    public async Task AllowAlwaysWildcardTool_ResolvesTheSessionWithAWildcardAlwaysRule()
    {
        var (vm, session) = await StartedVm();
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "Tool: Bash")
        {
            ToolUseId = "toolu_2",
            ToolName = "Bash",
            InputJson = """{"command":"ls"}""",
            IsPendingPermission = true,
        };

        await vm.AllowAlwaysWildcardToolCommand.ExecuteAsync(entry);

        await session.Received(1).AllowPermissionAlwaysAsync(
            "toolu_2", "Bash", """{"command":"ls"}""", PermissionRuleScope.Wildcard, Arg.Any<CancellationToken>());
    }

    private static SessionViewModel NewVm()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        return new SessionViewModel(new SessionManager(FactoryFor(session)));
    }

    /// <summary>A started session (its event loop is live), so send-path tests exercise sending after start rather than the not-started guard (#16).</summary>
    [Fact]
    public async Task SdkSession_WhenAutoSubmitOn_SendsTheTranscriptRightAfterInjection()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var voice = Substitute.For<IVoicePushToTalkService>();
        voice.BeginHold().Returns(true);
        voice.EndHoldAsync(Arg.Any<CancellationToken>()).Returns("open the file");
        var voiceSettings = Substitute.For<IVoiceSettingsStore>();
        voiceSettings.LoadAsync(Arg.Any<CancellationToken>()).Returns(
            new VoiceSettings { IsEnabled = true, PushToTalkKeyName = "F9", AutoSubmitAfterVoice = true });

        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)), voice, voiceSettings);
        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);
        for (var i = 0; i < 50 && !vm.AutoSubmitAfterVoice; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(vm.BeginVoiceHold());
        await vm.EndVoiceHoldAsync();

        // Auto-submit sent the appended transcript rather than leaving it in the input box for review.
        await session.Received(1).SendUserMessageAsync("open the file", Arg.Any<IReadOnlyList<ImageAttachment>>(), Arg.Any<CancellationToken>());
        Assert.Empty(vm.InputText);

        await vm.DisposeAsync();
    }

    // AC-1239: a launch that threw used to leave nothing a waiter could read — `IsSessionReady` false, the same
    // answer a session still coming up gives — so a start that died in 76 ms was waited out for a minute.
    [Fact]
    public async Task AStartThatThrows_NamesItsReason_RatherThanReadingAsStillComingUp()
    {
        var vm = new SessionViewModel(ManagerFor(RuntimeThatFailsWith(new InvalidOperationException("codex not found"))));

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        Assert.Equal("codex not found", vm.StartFailure);
        Assert.False(vm.IsSessionReady);
        // The pair that carries the distinction: not ready AND no longer starting means failed, not slow.
        Assert.False(vm.IsStarting);
        Assert.Contains("codex not found", vm.Status, StringComparison.Ordinal);

        await vm.DisposeAsync();
    }

    // The quieter half of the same defect: StartAsync returned without throwing and nothing is running, which left
    // Status reading "Session started." on a session that never did.
    [Fact]
    public async Task AStartThatReturnsWithNothingRunning_IsReportedAsFailedToo()
    {
        var vm = new SessionViewModel(ManagerFor(Substitute.For<ISessionRuntime>()));

        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        Assert.NotNull(vm.StartFailure);
        Assert.False(vm.IsSessionReady);
        Assert.DoesNotContain("Session started", vm.Status, StringComparison.Ordinal);

        await vm.DisposeAsync();
    }

    // The control the two above are only meaningful against: a start that took reports no failure. Without it a
    // StartFailure that is always set would pass them both and tell a waiter nothing.
    [Fact]
    public async Task AStartThatTook_ReportsNoFailure()
    {
        var (vm, _) = await StartedVm();

        Assert.Null(vm.StartFailure);
        Assert.True(vm.IsSessionReady);

        await vm.DisposeAsync();
    }

    private static ISessionRuntime RuntimeThatFailsWith(Exception failure)
    {
        var runtime = Substitute.For<ISessionRuntime>();
        runtime.StartAsync(
            Arg.Any<SessionProfile?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlySet<string>?>(),
            Arg.Any<string?>(), Arg.Any<SessionResume?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw failure);
        return runtime;
    }

    private static ISessionManager ManagerFor(ISessionRuntime runtime)
    {
        var manager = Substitute.For<ISessionManager>();
        manager.Create(Arg.Any<SessionProfile?>()).Returns(runtime);
        return manager;
    }

    private static async Task<(SessionViewModel Vm, ISessionDriver Session)> StartedVm()
    {
        var session = Substitute.For<ISessionDriver>();
        session.Events.Returns(EmptyEvents());
        var vm = new SessionViewModel(new SessionManager(FactoryFor(session)));
        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);
        return (vm, session);
    }

    private static async IAsyncEnumerable<SessionEvent> EmptyEvents([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Open until the runtime cancels it: a live driver's stream ends only when its process does (AC-693).
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }

    /// <summary>Wraps a fake driver in a factory so the view model resolves exactly that driver when it starts (the driver is now created from the factory once the profile is known).</summary>
    private static ISessionDriverFactory FactoryFor(ISessionDriver driver)
    {
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        return factory;
    }
}
