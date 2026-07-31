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
        Assert.Equal(new[] { new SessionRateWindow("5h", 60, reset), new SessionRateWindow("wk", 80, null) }, vm.RateLimits);
        Assert.Contains("Context window: 25% used", vm.LimitsTooltip);

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
        Assert.True(vm.ShowTokenMeter);
        Assert.Equal("3.0k tok · $0.0500", vm.UsageSummary);
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
        Assert.False(vm.ShowTokenMeter);
    }

    [Fact]
    public void TurnCompleted_WithNoUsage_NeverShowsTheTokenMeter()
    {
        // AC-536 AC3: a provider that reports no tokens must never surface a "0 tok" meter.
        var vm = NewVm();

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false, Usage = null });

        Assert.False(vm.HasUsage);
        Assert.False(vm.ShowTokenMeter);
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
        vm.IsAwaitingResponse = true; // a dispatched turn leaves it up until the first *visible* output

        vm.Apply(new AssistantThinkingDelta { SessionId = "S1", BlockIndex = 0, Thinking = "Pondering..." });

        // AC-213 revises AC-144: reasoning deltas stream into a dimmed, collapsible Thinking row on Developer.
        Assert.Single(vm.Transcript, t => t.Kind == TranscriptEntryKind.Thinking && t.Text == "Pondering...");
        Assert.True(vm.Transcript.Single().IsRowVisible);
        // The pulsing indicator is separate from the row and stays lit — thinking is still not "visible output",
        // so dousing it here would leave a gap where the session read as idle while the answer was still coming.
        Assert.True(vm.IsAwaitingResponse);
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

    [Fact]
    public void Apply_FirstAssistantOutput_ClearsTheThinkingIndicator()
    {
        var vm = NewVm();
        vm.IsAwaitingResponse = true; // as a dispatched turn leaves it until the first sign of activity

        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "hi" });

        Assert.False(vm.IsAwaitingResponse);
    }

    [Fact]
    public void Apply_NonOutputEvent_LeavesTheThinkingIndicatorUp()
    {
        var vm = NewVm();
        vm.IsAwaitingResponse = true;

        // A connect/status event is not the assistant answering, so the model is still "thinking".
        vm.Apply(new SessionInitialized { SessionId = "S1", Cwd = "", Tools = [] });

        Assert.True(vm.IsAwaitingResponse);
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

    [Fact]
    public void ErrorRow_StaysOnThePlainPath()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.Error, "Send failed: boom");

        Assert.False(entry.IsAssistantMarkdown);
        Assert.True(entry.IsPlainNonMarkdown);
    }

    [Fact]
    public void Apply_TurnCompleted_SetsStatusToDone()
    {
        var vm = NewVm();

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        Assert.Equal(SessionStatus.Done, vm.SessionStatus);
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
        vm.QueuedMessages.Add(new QueuedMessageViewModel("look at these", [imageA], m => vm.QueuedMessages.Remove(m)));
        vm.QueuedMessages.Add(new QueuedMessageViewModel("", [imageB], m => vm.QueuedMessages.Remove(m)));

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
        vm.QueuedMessages.Add(new QueuedMessageViewModel("", [imageA], m => vm.QueuedMessages.Remove(m)));
        vm.QueuedMessages.Add(new QueuedMessageViewModel("   ", [imageB], m => vm.QueuedMessages.Remove(m)));

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        // Every queued chip was image-only, so the combined text is empty but all images still go out together.
        await session.Received(1).SendUserMessageAsync(
            "",
            Arg.Is<IReadOnlyList<ImageAttachment>>(images => images.Count == 2),
            Arg.Any<CancellationToken>());
        Assert.Empty(vm.QueuedMessages);
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
        voice.EndHoldAsync(applyCleanup: true, Arg.Any<CancellationToken>()).Returns("open the file");
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
        await vm.EndVoiceHoldAsync(applyCleanup: true);

        // Auto-submit sent the appended transcript rather than leaving it in the input box for review.
        await session.Received(1).SendUserMessageAsync("open the file", Arg.Any<IReadOnlyList<ImageAttachment>>(), Arg.Any<CancellationToken>());
        Assert.Empty(vm.InputText);

        await vm.DisposeAsync();
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
        await Task.CompletedTask;
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
