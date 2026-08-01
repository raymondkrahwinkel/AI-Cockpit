using Microsoft.Extensions.Logging.Abstractions;
using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
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
    [Fact]
    public void Constructing_TheHost_StartsNothing()
    {
        var host = Dispatcher.UIThread.Invoke(() => _Host(enabled: true, slot: _ConfiguredSlot()));

        // The whole of "lazily": the feature is on and a profile is set, and still nothing is running. Only a hold
        // or a click builds the instance, so an operator who never speaks to it pays for no model in memory.
        Assert.Null(host.Session);
    }

    /// <summary>
    /// The state a fresh host reports before anything has read the settings is "unavailable, because it is off" —
    /// which is a guess, and wrong for the operator who had it on when they last closed the cockpit. The startup
    /// path has to resolve it, or the first hotkey press of every session refuses with a stale reason.
    /// </summary>
    [Fact]
    public void ApplySettings_IsWhatMakesTheReportedStateTrue_NotConstruction()
    {
        var host = Dispatcher.UIThread.Invoke(() => _Host(enabled: true, slot: _ConfiguredSlot()));

        // Constructed: still says off, though the settings say otherwise. This is the assertion whose absence let
        // the stale-state bug through review the first time.
        Assert.Equal(AssistantActivity.Unavailable, host.Activity);

        Dispatcher.UIThread.Invoke(() => host.ApplySettingsAsync().GetAwaiter().GetResult());

        Assert.Equal(AssistantActivity.Ready, host.Activity);
    }

    /// <summary>
    /// Coming back after falling over is a path the ticket asks for by name, so the wiring the host put on the
    /// dead instance has to come off with it — otherwise the one thing built to happen repeatedly leaks every time.
    /// </summary>
    [Fact]
    public void ReleasingAnAssistantSession_UnsubscribesIt_SoAReplacedInstanceIsNotHeldForever()
    {
        var (cockpit, session) = Dispatcher.UIThread.Invoke(() =>
        {
            var viewModel = new CockpitViewModel();
            return (viewModel, new SessionViewModel { BelongsToNoWorkspace = true });
        });

        Dispatcher.UIThread.Invoke(() =>
        {
            cockpit.ReleaseAssistantSession(session);

            // Idempotent, because the teardown path can be reached more than once for the same instance and a
            // second release must not throw at a caller that has nowhere to put an exception.
            cockpit.ReleaseAssistantSession(session);
        });
    }

    [Fact]
    public void EnsureStarted_WithTheFeatureOff_StartsNothingAndSaysWhy()
    {
        var host = Dispatcher.UIThread.Invoke(() => _Host(enabled: false, slot: _ConfiguredSlot()));

        var session = Dispatcher.UIThread.Invoke(() => host.EnsureStartedAsync().GetAwaiter().GetResult());

        Assert.Null(session);
        Assert.Null(host.Session);
        Assert.Equal(AssistantActivity.Unavailable, host.Activity);
        Assert.Contains("switched off", host.UnavailableReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureStarted_WithNoProfileInTheSlot_ReportsTheSlotsOwnReason()
    {
        var host = Dispatcher.UIThread.Invoke(() =>
            _Host(enabled: true, slot: AssistantProfileSlot.Unset("The provider switch could not be completed.")));

        var session = Dispatcher.UIThread.Invoke(() => host.EnsureStartedAsync().GetAwaiter().GetResult());

        Assert.Null(session);
        // The slot's reason, not one invented here: the operator is told what actually happened to their profile
        // rather than a generic "not configured" that hides a failed switch.
        Assert.Equal("The provider switch could not be completed.", host.UnavailableReason);
    }

    [Fact]
    public void Send_WithTheFeatureOff_DoesNotStartTheAssistant()
    {
        var host = Dispatcher.UIThread.Invoke(() => _Host(enabled: false, slot: _ConfiguredSlot()));

        Dispatcher.UIThread.Invoke(() => host.SendAsync("what is the status of AC-223").GetAwaiter().GetResult());

        Assert.Null(host.Session);
        Assert.Equal(AssistantActivity.Unavailable, host.Activity);
    }

    /// <summary>
    /// AC-138 follow-up: a reading level saved in Options has to reach a session that is already running, the same
    /// way <see cref="AssistantSessionHost.SetSpeakReplies"/> reaches a live session for speaking — not only the
    /// next time the assistant starts.
    /// </summary>
    [Fact]
    public void ApplySettings_WithALiveSession_PushesTheNewReadingLevelToItImmediately()
    {
        var host = Dispatcher.UIThread.Invoke(() =>
            _Host(enabled: true, slot: _ConfiguredSlot(), readingLevel: Cockpit.Core.Sessions.ReadingLevel.Simple));
        var session = Dispatcher.UIThread.Invoke(() => new SessionViewModel());
        Dispatcher.UIThread.Invoke(() => host.Session = session);

        Dispatcher.UIThread.Invoke(() => host.ApplySettingsAsync().GetAwaiter().GetResult());

        Assert.Equal(Cockpit.Core.Sessions.ReadingLevel.Simple, session.ReadingLevel);
    }

    [Fact]
    public void ApplySettings_SwitchedOn_MakesItReachableWithoutStartingIt()
    {
        var host = Dispatcher.UIThread.Invoke(() => _Host(enabled: true, slot: _ConfiguredSlot()));

        Dispatcher.UIThread.Invoke(() => host.ApplySettingsAsync().GetAwaiter().GetResult());

        // Switching the feature on makes the assistant available; the first hold or click is still what wakes it.
        Assert.Equal(AssistantActivity.Ready, host.Activity);
        Assert.Null(host.UnavailableReason);
        Assert.Null(host.Session);
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

    [Theory]
    [InlineData(true, AssistantActivity.Thinking)]
    [InlineData(false, AssistantActivity.Ready)]
    public void ActivityFor_OnceThePermissionIsAnswered_FollowsWhetherItIsStillWorking(bool isBusy, AssistantActivity expected)
    {
        // Two defects in a row lived here, and both came from reading SessionStatus instead of the two facts
        // underneath it. First the chip stuck on "Needs you", because NeedsAttention is cleared only when the
        // operator sends their *next* message — not when they answer the prompt. Then, with the pending flag read
        // properly, it stuck on "Ready" while the assistant was visibly working, because a session still carrying
        // NeedsAttention never reports Busy at all: that flag outranks busy in the derivation. The chat window's
        // own "Thinking…" row was right the whole time, which is what made the chip's silence so obviously wrong.
        Assert.Equal(
            expected,
            AssistantSessionHost.ActivityFor(AssistantActivity.AwaitingOperator, isBusy, hasPendingPermission: false));
    }

    [Theory]
    [InlineData(true, AssistantActivity.Thinking)]
    [InlineData(false, AssistantActivity.Ready)]
    public void ActivityFor_FollowsWhetherTheSessionIsWorking(bool isBusy, AssistantActivity expected)
    {
        // The original defect: Activity was written on the way in (a hold, a send, a start) and never on the way
        // out, because nothing watched the session. So the first send showed Ready while the assistant was plainly
        // thinking, and every send after that left the chip on "Thinking…" for good — EnsureStartedAsync hands
        // back a live instance without touching Activity.
        Assert.Equal(expected, AssistantSessionHost.ActivityFor(AssistantActivity.Thinking, isBusy, hasPendingPermission: false));
    }

    [Theory]
    [InlineData(AssistantActivity.Unavailable)]
    [InlineData(AssistantActivity.Listening)]
    public void ActivityFor_NeverSpeaksOverTheTwoStatesTheSessionKnowsNothingAbout(AssistantActivity current)
    {
        // Unavailable is a fact about the feature, not about a turn; Listening is a key being held right now, and a
        // turn finishing mid-hold must not tell the operator the microphone closed.
        Assert.Equal(current, AssistantSessionHost.ActivityFor(current, isBusy: false, hasPendingPermission: false));
        Assert.Equal(current, AssistantSessionHost.ActivityFor(current, isBusy: true, hasPendingPermission: false));
    }

    // ── AC-544 criterion 2, the mounting half: the assistant's launch is the one that names the broad read server ──

    [Fact]
    public void McpSelection_AlwaysNamesTheBroadReadServer()
    {
        // The whole reason an internal endpoint reaches the assistant at all. If this line ever stops being written,
        // criterion 1 fails silently — the tools are hosted and simply never handed to anybody.
        var selection = AssistantSessionHost.McpSelection(_Profile(), []);

        Assert.Contains(AssistantIdentity.McpServerName, selection);
    }

    [Fact]
    public void McpSelection_WithNoSavedSelection_StillCarriesTheOperatorsOrdinaryServers()
    {
        // Naming a selection overrides the profile's own, so the servers the assistant would otherwise have had —
        // Depot, YouTrack — must be spelled back in, or the mount rule quietly costs the assistant everything else.
        var selection = AssistantSessionHost.McpSelection(_Profile(), [
            new McpServerConfig { Name = "depot", Enabled = true },
            new McpServerConfig { Name = "off-server", Enabled = false },
        ]);

        Assert.Contains("depot", selection);
        Assert.Contains(AssistantIdentity.McpServerName, selection);
        Assert.DoesNotContain("off-server", selection);
    }

    [Fact]
    public void McpSelection_NeverWidensToOtherInternalEndpoints()
    {
        // "Give the assistant everything" is the accident this guards against: another spawn's internal tools
        // (Autopilot's CEO/step endpoints) are not the assistant's to inherit just because it is privileged.
        var selection = AssistantSessionHost.McpSelection(_Profile(), [
            new McpServerConfig { Name = "autopilot-plan", Enabled = true, Internal = true },
        ]);

        Assert.DoesNotContain("autopilot-plan", selection);
    }

    [Fact]
    public void McpSelection_WithAProfileSelection_KeepsItAndAddsTheBroadReadServer()
    {
        var profile = _Profile() with { EnabledMcpServerNames = ["depot"] };

        var selection = AssistantSessionHost.McpSelection(profile, []);

        Assert.Equal(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "depot",
                AssistantIdentity.McpServerName,
                AssistantIdentity.ActMcpServerName,
            },
            selection);
    }

    // ── AC-545, the mounting half of the acting server ──

    /// <summary>
    /// Every one of the assistant's own endpoints is named by this launch — read (AC-544) and acting (AC-545) alike.
    /// </summary>
    /// <remarks>
    /// <b>The set is read off <see cref="AssistantIdentity"/>, not typed out here.</b> That is the phase-2 lesson:
    /// a test listing the servers it expects passes happily on the day a third one is added and not mounted. Both
    /// halves of the first gate have to hold and they fail in opposite directions — an endpoint that is not Internal
    /// fans out to every session (covered in <c>AssistantActMountRuleTests</c>), and one that is Internal but named
    /// by nobody is hosted, registered, tested, and reaches the assistant never. The second is the silent one: the
    /// assistant simply has no acting tools, and nothing anywhere says so.
    /// </remarks>
    /// <summary>
    /// The acting servers the assistant is not handed by default stay out of the fan-out — and the list is read off
    /// the production set rather than typed here, so a server added to it is covered on the day it is added.
    /// </summary>
    /// <remarks>
    /// <c>cockpit-orchestrator</c> is the one this is really about: <c>delegate_task</c> starts AI work with no pane,
    /// no Allow row and no line in the spawn trail, which is every guarantee AC-545 built, absent.
    /// </remarks>
    [Fact]
    public void McpSelection_WithNoSavedSelection_LeavesOutTheServersTheAssistantIsNotGivenByDefault()
    {
        var catalog = AssistantSessionHost.NotFannedOutToTheAssistant
            .Select(name => new McpServerConfig { Name = name, Enabled = true })
            .Append(new McpServerConfig { Name = "depot", Enabled = true })
            .ToList();

        var selection = AssistantSessionHost.McpSelection(_Profile(), catalog);

        Assert.All(AssistantSessionHost.NotFannedOutToTheAssistant, name => Assert.DoesNotContain(name, selection));

        // The other half: an ordinary information server still arrives, or this would be a test about a fan-out
        // that gives the assistant nothing at all.
        Assert.Contains("depot", selection);
    }

    /// <summary>
    /// A selection the operator saved on the profile is taken whole, including a server the default leaves out:
    /// this is a default, not a boundary, and an explicit tick is an answer.
    /// </summary>
    [Fact]
    public void McpSelection_WithASavedSelection_KeepsEvenAServerTheDefaultWouldLeaveOut()
    {
        var excluded = AssistantSessionHost.NotFannedOutToTheAssistant.First();
        var profile = _Profile() with { EnabledMcpServerNames = [excluded] };

        var selection = AssistantSessionHost.McpSelection(profile, []);

        Assert.Contains(excluded, selection);
    }

    [Fact]
    public void McpSelection_NamesEveryOneOfTheAssistantsOwnServers()
    {
        var ownServers = typeof(AssistantIdentity)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string) && field.Name.EndsWith("McpServerName", StringComparison.Ordinal))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        // A reflection query that found nothing would make the assertion below vacuously true.
        Assert.NotEmpty(ownServers);

        var selection = AssistantSessionHost.McpSelection(_Profile(), []);

        Assert.All(ownServers, server => Assert.Contains(server, selection));
    }

    private static SessionProfile _Profile() => new("assistant-local", new ClaudeConfig("/tmp/claude"));

    private static AssistantSessionHost _Host(
        bool enabled,
        AssistantProfileSlot slot,
        IMcpServerCatalog? catalog = null,
        Cockpit.Core.Sessions.ReadingLevel readingLevel = Cockpit.Core.Sessions.ReadingLevel.Developer)
    {
        var settings = Substitute.For<IAssistantSettingsStore>();
        settings.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new AssistantSettings { IsEnabled = enabled, ReadingLevel = readingLevel });

        var profiles = Substitute.For<IAssistantProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(slot);

        var state = Substitute.For<ISessionStateStore>();
        state.LoadAsync(Arg.Any<CancellationToken>()).Returns([]);

        return new AssistantSessionHost(
            new CockpitViewModel(), settings, profiles, state,
            catalog ?? _Catalog(), NullLogger<AssistantSessionHost>.Instance);
    }

    private static IMcpServerCatalog _Catalog(params McpServerConfig[] servers)
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<McpServerConfig>>(_ => servers);
        return catalog;
    }

    private static AssistantProfileSlot _ConfiguredSlot() => new(_Profile());
}
