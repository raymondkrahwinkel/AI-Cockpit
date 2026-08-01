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

    [Fact]
    public void ActivityFor_WhileAPermissionIsActuallyPending_SaysItNeedsYou()
    {
        Assert.Equal(
            AssistantActivity.AwaitingOperator,
            AssistantSessionHost.ActivityFor(AssistantActivity.Thinking, SessionStatus.Busy, hasPendingPermission: true));
    }

    [Theory]
    [InlineData(SessionStatus.NeedsAttention)]
    [InlineData(SessionStatus.Done)]
    public void ActivityFor_OnceThePermissionIsAnswered_StopsSayingItNeedsYou(SessionStatus status)
    {
        // The defect this pins: the first version read SessionStatus.NeedsAttention, which is sticky by design —
        // it is cleared only when the operator sends their next message, not when they answer the prompt. So the
        // chip sat on "Needs you" through the rest of the answer and the read-aloud, and because that state
        // outranks the others it never showed Thinking or Speaking again either. A test that only drove the
        // status enum would have gone on passing.
        Assert.Equal(
            AssistantActivity.Ready,
            AssistantSessionHost.ActivityFor(AssistantActivity.AwaitingOperator, status, hasPendingPermission: false));
    }

    [Theory]
    [InlineData(SessionStatus.Busy, AssistantActivity.Thinking)]
    [InlineData(SessionStatus.WorkingBackground, AssistantActivity.Thinking)]
    [InlineData(SessionStatus.Idle, AssistantActivity.Ready)]
    [InlineData(SessionStatus.Done, AssistantActivity.Ready)]
    [InlineData(SessionStatus.WaitingForInput, AssistantActivity.Ready)]
    public void ActivityFor_FollowsTheSessionsOwnStatus(SessionStatus status, AssistantActivity expected)
    {
        // The defect this pins was visible on the very first real use and invisible to every test: Activity was
        // written on the way in (a hold, a send, a start) and never on the way out, because nothing watched the
        // session. So the first send showed Ready while the assistant was plainly thinking, and every send after
        // that left the chip on "Thinking…" for good — EnsureStartedAsync hands back a live instance without
        // touching Activity.
        Assert.Equal(expected, AssistantSessionHost.ActivityFor(AssistantActivity.Thinking, status, hasPendingPermission: false));
    }

    [Theory]
    [InlineData(AssistantActivity.Unavailable)]
    [InlineData(AssistantActivity.Listening)]
    public void ActivityFor_NeverSpeaksOverTheTwoStatesTheSessionKnowsNothingAbout(AssistantActivity current)
    {
        // Unavailable is a fact about the feature, not about a turn; Listening is a key being held right now, and a
        // turn finishing mid-hold must not tell the operator the microphone closed.
        Assert.Equal(current, AssistantSessionHost.ActivityFor(current, SessionStatus.Idle, hasPendingPermission: false));
        Assert.Equal(current, AssistantSessionHost.ActivityFor(current, SessionStatus.Busy, hasPendingPermission: false));
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
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "depot", AssistantIdentity.McpServerName },
            selection);
    }

    private static SessionProfile _Profile() => new("assistant-local", new ClaudeConfig("/tmp/claude"));

    private static AssistantSessionHost _Host(bool enabled, AssistantProfileSlot slot, IMcpServerCatalog? catalog = null)
    {
        var settings = Substitute.For<IAssistantSettingsStore>();
        settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new AssistantSettings { IsEnabled = enabled });

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
