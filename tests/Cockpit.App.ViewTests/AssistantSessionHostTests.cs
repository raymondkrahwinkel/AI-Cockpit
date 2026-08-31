using Microsoft.Extensions.Logging.Abstractions;
using Avalonia.Threading;
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
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Sessions;
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
    /// Everything an Options save decides about a running assistant reaches it now, not at its next start — every
    /// field, not one of them.
    /// </summary>
    /// <remarks>
    /// This test used to assert the reading level alone while its own summary claimed the pair of them moved
    /// together, and the half it did not assert was broken the whole time: <c>SpeakReplies</c> was pushed only by
    /// <c>_ApplySpeech</c> at start, so ticking "speak replies" in Options moved the header checkbox — the chat view
    /// model applies a loaded value under a guard that suppresses its own push to the host — and left the live
    /// session on the value it had started with. Measured as <c>settingsSpeakReplies=True
    /// liveSessionReadResponsesAloud=False</c>: the operator was told the assistant would speak, and it did not.
    /// <para>
    /// So this asserts the whole set the start path writes. A field added to <c>_ApplySpeech</c> and not to the
    /// live-apply is the exact shape of that defect, and the assertion below is what notices.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApplySettings_WithALiveSession_PushesEverySpokenSettingToIt_NotOnlyTheReadingLevel(bool speakReplies)
    {
        var cockpit = Dispatcher.UIThread.Invoke(() => new CockpitViewModel
        {
            SelectedTtsVoice = TtsVoiceCatalog.Voices[^1],
            SelectedReadAloudLanguage = new SttLanguageOption("Dutch", "nl"),
        });
        var host = Dispatcher.UIThread.Invoke(() => _Host(
            enabled: true,
            slot: _ConfiguredSlot(),
            readingLevel: Cockpit.Core.Sessions.ReadingLevel.Simple,
            speakReplies: speakReplies,
            cockpit: cockpit));

        // The state a session that started before the save is in — the opposite of every value about to be saved,
        // so nothing below can pass by having been right already.
        var session = Dispatcher.UIThread.Invoke(() => new SessionViewModel
        {
            ReadingLevel = Cockpit.Core.Sessions.ReadingLevel.Developer,
            ReadResponsesAloud = !speakReplies,
            ReadAloudLanguage = "en",
            TtsVoiceSid = -1,
            ReadAloudAsOneUtterance = false,
        });
        Dispatcher.UIThread.Invoke(() => host.Session = session);

        Dispatcher.UIThread.Invoke(() => host.ApplySettingsAsync().GetAwaiter().GetResult());

        Assert.Equal(Cockpit.Core.Sessions.ReadingLevel.Simple, session.ReadingLevel);
        Assert.Equal(speakReplies, session.ReadResponsesAloud);
        Assert.Equal(TtsVoiceCatalog.Voices[^1].Sid, session.TtsVoiceSid);
        Assert.Equal("nl", session.ReadAloudLanguage);
        Assert.True(session.ReadAloudAsOneUtterance);
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

    // AC-869: the assistant always has cockpit-github-pull-requests, regardless of its working directory — the
    // git-repo rule other sessions get it through does not apply to the assistant at all.
    [Fact]
    public void McpSelection_AlwaysNamesTheGitHubPullRequestsServer()
    {
        var selection = AssistantSessionHost.McpSelection(_Profile(), []);

        Assert.Contains(GitHubPullRequestsMcp.ServerName, selection);
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

    // AC-766: this is the mount-side half of the fix — a plugin-provided server the catalog now offers unscoped
    // (Depot, say) is not marked ProjectLinked there (no single project points at it in that call), and the
    // no-selection fan-out must carry it through anyway; ProjectLinked is not one of the things it checks.
    [Fact]
    public void McpSelection_WithNoSavedSelection_IncludesAPluginServerNotTiedToAnyProject()
    {
        var selection = AssistantSessionHost.McpSelection(_Profile(), [
            new McpServerConfig { Name = "Depot: wispslate", Enabled = true, ProjectLinked = false },
        ]);

        Assert.Contains("Depot: wispslate", selection);
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
                GitHubPullRequestsMcp.ServerName,
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

    // ── The assistant runs on its own profile's start defaults, not on the app's ──────────────────────────────
    //
    // The measured failure: the operator put bypassPermissions on the Assistant Profile and was still asked to
    // confirm every tool call. A plugin profile carries its permission mode, model and effort in the generic
    // OptionDefaults map — not in the legacy typed Defaults.PermissionMode/Model/Effort, which exist only for the
    // one-time migration — and that map reaches a driver as launch options. The assistant's launch built a map
    // holding nothing but its system prompt, so the profile was passed along and then, on the three settings that
    // decide what it may do, ignored.

    [Fact]
    public void LaunchOptions_CarryTheProfilesOwnPermissionMode()
    {
        var options = AssistantSessionHost._LaunchOptions(_ProfileWithDefaults(
            (WellKnownPluginSessionOptions.PermissionMode, SessionOptionCatalog.BypassPermissionModeValue)), replacesStandingInstruction: false, memory: null);

        Assert.Equal(
            SessionOptionCatalog.BypassPermissionModeValue,
            options[WellKnownPluginSessionOptions.PermissionMode]);
    }

    // ── AC-759: the acting paragraph's SDK gate agrees with the profile's own permission mode ────────────────────

    [Fact]
    public void SdkAsksPermission_TrueForAProfileThatNamesNoPermissionMode()
    {
        // The app floor (SessionOptionCatalog.DefaultPermissionMode) is a confining mode, so a profile that says
        // nothing still asks — the same fallback StartConfiguredAsync's own typed argument falls back to.
        Assert.True(AssistantSessionHost._SdkAsksPermission(_Profile()));
    }

    [Fact]
    public void SdkAsksPermission_FalseForAProfileSetToBypassPermissions()
    {
        var profile = _ProfileWithDefaults(
            (WellKnownPluginSessionOptions.PermissionMode, SessionOptionCatalog.BypassPermissionModeValue));

        Assert.False(AssistantSessionHost._SdkAsksPermission(profile));
    }

    [Fact]
    public void LaunchOptions_OnABypassPermissionsProfile_ComposesTheGateBypassedParagraph()
    {
        var options = AssistantSessionHost._LaunchOptions(
            _ProfileWithDefaults((WellKnownPluginSessionOptions.PermissionMode, SessionOptionCatalog.BypassPermissionModeValue)),
            replacesStandingInstruction: false,
            memory: null,
            currentState: null,
            sdkAsksPermission: false,
            consentCardAsks: true);

        var instruction = options[WellKnownPluginSessionOptions.AppendSystemPrompt];

        Assert.NotEqual(AssistantSystemPrompt.Default, instruction);
        Assert.Contains("set to bypass permissions, so the call simply goes ahead", instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchOptions_CarryTheProfilesOwnModelAndEffort()
    {
        // The same map, the same route, the same driver read (ClaudeSdkSessionDriver._ResolveOption) — so a fix
        // that only reached the permission mode would be a fix for one of three settings that were all lost the
        // same way. The keys are the plugin's own, which is why they are literals here rather than host constants:
        // the host does not (and must not) know what a provider's options mean.
        var options = AssistantSessionHost._LaunchOptions(_ProfileWithDefaults(
            ("model", "opus"),
            ("effort", "high")), replacesStandingInstruction: false, memory: null);

        Assert.Equal("opus", options["model"]);
        Assert.Equal("high", options["effort"]);
    }

    [Fact]
    public void LaunchOptions_ForAProfileThatSaysNothing_NameNoneOfThem_SoTheAppDefaultStillStands()
    {
        // The half that protects everyone who has set nothing. The typed permission mode/model/effort at the call
        // site stay on the app defaults and PluginSessionDriverAdapter._MergePermissionMode folds the typed value
        // in only when the options carry none — so an absent key is what makes today's behaviour survive this
        // change. A "helpful" default written here (say, always naming the mode) would silently move every
        // existing assistant. The closing assertion carries the other half: the two gate parameters default to
        // "still asks" (AC-759), so a call site that has never been taught about them composes exactly the
        // appended prompt it always composed.
        var options = AssistantSessionHost._LaunchOptions(_Profile(), replacesStandingInstruction: false, memory: null);

        Assert.False(options.ContainsKey(WellKnownPluginSessionOptions.PermissionMode));
        Assert.False(options.ContainsKey("model"));
        Assert.False(options.ContainsKey("effort"));

        // And the one thing the launch does owe it is still there.
        Assert.Equal(AssistantSystemPrompt.Default, options[WellKnownPluginSessionOptions.AppendSystemPrompt]);
    }

    [Fact]
    public void LaunchOptions_CarryTheRuleThatTheAssistantImplementsNothingItself()
    {
        // AC-639. The rule exists because the assistant built and fixed straight in a checkout rather than
        // spawning for it (AC-638), and the capability map is the only place it is written down. Asserted on what
        // a launch actually hands the session rather than on the constant: that the constant reads well is not the
        // claim — that the rule arrives is. The other asserts in this file compare `Default` with itself and would
        // pass just as happily with this paragraph deleted, which is exactly why this one names the text.
        var options = AssistantSessionHost._LaunchOptions(_Profile(), replacesStandingInstruction: false, memory: null);

        var instruction = options[WellKnownPluginSessionOptions.AppendSystemPrompt];
        Assert.Contains("YOU DO NOT IMPLEMENT", instruction, StringComparison.Ordinal);
        Assert.Contains("on its own worktree", instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchOptions_CarryWhereToLookBeforeAssumingWhatAProfileRunsAt()
    {
        // AC-647, asserted the same way as AC-639 above: on what a launch actually hands the session, because that
        // the constant reads well is not the claim. The gap it closes is a spawn picked on a label — the profile
        // that turned out to run in a bypass permission mode on the costly model was indistinguishable from one
        // that did not, and both read "Plugin" as their provider.
        var options = AssistantSessionHost._LaunchOptions(_Profile(), replacesStandingInstruction: false, memory: null);

        var instruction = options[WellKnownPluginSessionOptions.AppendSystemPrompt];
        Assert.Contains("`list_profiles` carries an `Options` list", instruction, StringComparison.Ordinal);
        Assert.Contains("Providers do not share a shape", instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchOptions_CarryThatASpawnMayOverrideOptions_AndThatThePermissionModeIsNeverOneOfThem()
    {
        // AC-648 criterion 6, asserted the way AC-639's test above is. Both halves matter: an assistant that never
        // learns the `options` map exists cannot honour "the same profile, but lighter", and one that learns it
        // without the exception will try the permission mode and spend a turn being refused.
        var options = AssistantSessionHost._LaunchOptions(_Profile(), replacesStandingInstruction: false, memory: null);

        var instruction = options[WellKnownPluginSessionOptions.AppendSystemPrompt];
        Assert.Contains("`start_agent` takes an `options` map", instruction, StringComparison.Ordinal);
        Assert.Contains("PERMISSION-MODE IS NEVER OVERRIDABLE", instruction, StringComparison.Ordinal);
        Assert.Contains("`list_profiles` showed for that profile", instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchOptions_CarryTheToolThatSeesDelegatedBackgroundWork()
    {
        // AC-641, asserted the way AC-639's test above is: off the instruction a launch actually delivers, not off
        // the constant. A tool the map never names is a tool the assistant does not know it has (AC-635), and this
        // one is the only route to work that appears in no session list at all.
        var options = AssistantSessionHost._LaunchOptions(_Profile(), replacesStandingInstruction: false, memory: null);

        var instruction = options[WellKnownPluginSessionOptions.AppendSystemPrompt];
        Assert.Contains("list_delegated_tasks", instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchOptions_CarryTheWatcherAndEveryEventItCanBeArmedFor()
    {
        // AC-640, asserted the way AC-639's test above is: off the instruction a launch actually delivers. Naming
        // only the two tools would leave the assistant knowing it can watch something and not what it can watch
        // for — and the point of the ticket is that it stops polling `list_sessions`, which it will not do for
        // events it cannot name.
        var options = AssistantSessionHost._LaunchOptions(_Profile(), replacesStandingInstruction: false, memory: null);

        var instruction = options[WellKnownPluginSessionOptions.AppendSystemPrompt];
        Assert.Contains("watch_session", instruction, StringComparison.Ordinal);
        Assert.Contains("unwatch_session", instruction, StringComparison.Ordinal);
        Assert.Contains("busy-to-idle", instruction, StringComparison.Ordinal);
        Assert.Contains("needs-attention", instruction, StringComparison.Ordinal);
        Assert.Contains("gone", instruction, StringComparison.Ordinal);
        Assert.Contains("stuck", instruction, StringComparison.Ordinal);
        Assert.Contains("pattern", instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchOptions_SayTheAssistantIsWokenByItsOwnWaitingMail_NotRefusedAsNotWakeable()
    {
        // AC-656, asserted the way AC-639's test above is: off the instruction a launch actually delivers. The old
        // line told the assistant an urgent notify would always be refused for it; that stopped being true, and a
        // capability map that still said so would send the assistant looking for a route (polling, a relayed
        // message) it no longer needs.
        var options = AssistantSessionHost._LaunchOptions(_Profile(), replacesStandingInstruction: false, memory: null);

        var instruction = options[WellKnownPluginSessionOptions.AppendSystemPrompt];
        Assert.Contains("AC-656", instruction, StringComparison.Ordinal);
        Assert.Contains("no opt-in", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("not-wakeable", instruction, StringComparison.Ordinal);
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
        Assert.EndsWith("You are Olaf.", instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchOptions_WhatTheOperatorWrote_IsAddedToTheBuiltInInstruction_RatherThanPutInItsPlace()
    {
        // AC-594. The old behaviour dropped all of AssistantSystemPrompt.Default the moment this box had anything in
        // it, so "your name is Zyra" silently cost the language rule and the whole permission paragraph.
        var profile = _Profile() with { SystemPrompt = "Your name is Zyra." };

        var options = AssistantSessionHost._LaunchOptions(profile, replacesStandingInstruction: false, memory: null);

        var instruction = options[WellKnownPluginSessionOptions.AppendSystemPrompt];
        Assert.StartsWith(AssistantSystemPrompt.Default, instruction, StringComparison.Ordinal);
        Assert.EndsWith("Your name is Zyra.", instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchOptions_WithTheAdvancedOptionOn_CarryWhatTheOperatorWroteAndNothingElse()
    {
        var profile = _Profile() with { SystemPrompt = "Your name is Zyra." };

        var options = AssistantSessionHost._LaunchOptions(profile, replacesStandingInstruction: true, memory: null);

        Assert.Equal("Your name is Zyra.", options[WellKnownPluginSessionOptions.AppendSystemPrompt]);
    }

    [Fact]
    public void LaunchOptions_CarryWhatTheAssistantWasAskedToRemember_UnderAHeadingOfItsOwn()
    {
        // AC-595. Last and labelled: it is the operator's material rather than the product's rules, and an
        // assistant that cannot tell the two apart recites a remembered line back as if it were one.
        var options = AssistantSessionHost._LaunchOptions(
            _Profile() with { SystemPrompt = "Your name is Zyra." },
            replacesStandingInstruction: false,
            memory: "- 2026-08-02 — The operator is called Raymond.");

        var instruction = options[WellKnownPluginSessionOptions.AppendSystemPrompt];
        Assert.StartsWith(AssistantSystemPrompt.Default, instruction, StringComparison.Ordinal);
        Assert.Contains("Your name is Zyra.", instruction, StringComparison.Ordinal);
        Assert.EndsWith("The operator is called Raymond.", instruction, StringComparison.Ordinal);
        Assert.Contains(AssistantStandingInstruction.MemoryHeading, instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchOptions_CarryTheNoteTheAssistantLeftItself_UnderItsOwnHeading()
    {
        // AC-596. Labelled as possibly stale on purpose: an assistant that read this as "what the operator just
        // said" would answer a question nobody asked again after every hand-over.
        var options = AssistantSessionHost._LaunchOptions(
            _Profile(),
            replacesStandingInstruction: false,
            memory: "- 2026-08-02 — The operator is called Raymond.",
            currentState: "We are on AC-592; the release desk is running the tests.");

        var instruction = options[WellKnownPluginSessionOptions.AppendSystemPrompt];
        Assert.Contains(AssistantStandingInstruction.MemoryHeading, instruction, StringComparison.Ordinal);
        Assert.Contains(AssistantStandingInstruction.CurrentStateHeading, instruction, StringComparison.Ordinal);
        Assert.EndsWith("the release desk is running the tests.", instruction, StringComparison.Ordinal);
    }

    // ── Handing over before the context fills (AC-596) ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(79.9, false, false, false)]
    [InlineData(80, false, false, true)]
    [InlineData(99, false, false, true)]
    // Never mid-turn: the restart would take the turn it is in the middle of with it.
    [InlineData(99, true, false, false)]
    // Never over a permission nobody has answered: that row belongs to a session that would no longer exist.
    [InlineData(99, false, true, false)]
    public void HandingOver_WaitsForAContextThatIsFull_AndForAnAssistantThatIsDoingNothing(
        double fill, bool isBusy, bool isWaitingOnOperator, bool expected) =>
        Assert.Equal(expected, AssistantSessionHost.ShouldHandOver(fill, isBusy, isWaitingOnOperator));

    [Fact]
    public void AProviderThatReportedNoFillThisTurn_DoesNotCountAsAnEmptyContext()
    {
        // The failure this rules out is the quiet one: reading "no reading" as zero postpones the hand-over for
        // ever on a provider that only reports sometimes, and the first anyone hears of it is a refused turn.
        Assert.False(AssistantSessionHost.ShouldHandOver(null, isBusy: false, isWaitingOnOperator: false));
    }

    /// <summary>
    /// AC-638: a hand-over swaps in a new <c>SessionViewModel</c> whose transcript is empty, which reads as data
    /// loss unless the new transcript says why.
    /// </summary>
    [Fact]
    public void HandingOver_LeavesADividerInTheNewSessionsTranscript_SoAnEmptyWindowIsNotReadAsDataLoss()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyEvents());
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);

        var cockpit = Dispatcher.UIThread.Invoke(() => _CockpitWithSessionFactory(
            () => new SessionViewModel(new SessionManager(factory))));
        var host = Dispatcher.UIThread.Invoke(() => _Host(enabled: true, slot: _ConfiguredSlot(), cockpit: cockpit));

        var first = Dispatcher.UIThread.Invoke(() => host.EnsureStartedAsync().GetAwaiter().GetResult());
        Assert.NotNull(first);
        Assert.DoesNotContain(first!.Transcript, entry => entry.IsDivider);

        // The same signal production reads: a turn completes with the context past the hand-over threshold.
        driver.CurrentStatus.Returns(new SessionStatusFeed(90, []));
        Dispatcher.UIThread.Invoke(() => first.Apply(new TurnCompleted
        {
            SessionId = "S1",
            Subtype = "success",
            Result = "done",
            IsError = false,
            Usage = new TokenUsage(1_000, 2_000, 0, 0),
            TotalCostUsd = 0.01,
        }));

        var second = _ReplacementOf(host, first);
        var divider = Assert.Single(second.Transcript, entry => entry.IsDivider);
        Assert.Contains("Context was full", divider.Text, StringComparison.Ordinal);
    }

    // ── Compacting instead of throwing the conversation away (AC-664) ─────────────────────────────────────────

    /// <summary>
    /// AC-664: the hand-over above is data loss — the transcript goes and only the standing instruction, the memory
    /// file and the last <c>note_state</c> carry across. A provider that can summarise its own conversation is asked
    /// to do that instead, and the instance stays exactly where it was.
    /// </summary>
    [Fact]
    public void AFullContext_OnAProviderThatCanCompactItself_IsCompacted_RatherThanRestartedOnAFreshConversation()
    {
        var (host, first, driver) = _StartedAssistantOn(_CompactingProvider());

        _ReportAFullContext(first, driver, fill: 90);

        // The same conversation, and the ask that keeps it: a hand-over would have swapped the instance for one whose
        // transcript is empty.
        Assert.Same(first, host.Session);
        driver.Received(1).CompactContextAsync(Arg.Any<CancellationToken>());

        // And it says so where the operator reads, since the provider reports a compaction nowhere they can see.
        var divider = Assert.Single(first.Transcript, entry => entry.IsDivider);
        Assert.Contains("continues here", divider.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Compacting is the better answer, not a guaranteed one — a provider can decline ("not enough messages to
    /// compact") and leave the context exactly as full. The restart stays as the floor under that: the ask is spent
    /// once, and a fill still above the line once the provider has answered gets the hand-over after all.
    /// </summary>
    [Fact]
    public void AFullContext_ThatCompactingDidNotRelieve_StillHandsOverToAFreshConversation()
    {
        var (host, first, driver) = _StartedAssistantOn(_CompactingProvider());

        _ReportAFullContext(first, driver, fill: 90);
        Assert.Same(first, host.Session);

        // The next reading with nothing running is the provider's answer landing — and it left the context as full
        // as it found it.
        _ReportAFullContext(first, driver, fill: 91);

        var second = _ReplacementOf(host, first);
        Assert.Contains(second.Transcript, entry => entry.IsDivider);

        // Asked once, not once per reading: the fill only moves after the provider answers, so an unguarded rule
        // would keep asking a provider that is still working on the first ask.
        driver.Received(1).CompactContextAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// And a compaction that <em>did</em> work re-arms the ask, so the next time the context fills the answer is
    /// another compaction rather than the hand-over this ticket exists to avoid.
    /// </summary>
    [Fact]
    public void AContextThatCompactingRelieved_EarnsAnotherCompaction_WhenItFillsUpAgain()
    {
        var (host, first, driver) = _StartedAssistantOn(_CompactingProvider());

        _ReportAFullContext(first, driver, fill: 90);
        _ReportAFullContext(first, driver, fill: 40);
        _ReportAFullContext(first, driver, fill: 90);

        Assert.Same(first, host.Session);
        driver.Received(2).CompactContextAsync(Arg.Any<CancellationToken>());
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
        Assert.Same(first, host.Session);

        // The compaction turn ends: busy drops while the provider's figure has not been re-read yet.
        Dispatcher.UIThread.Invoke(() => first.IsBusy = false);

        Assert.Same(first, host.Session);
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
        transcript.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<IReadOnlyList<TranscriptSnapshotEntry>>(_ =>
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
        transcript.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<IReadOnlyList<TranscriptSnapshotEntry>>(_ =>
        [
            _Recorded("SomeFutureKind", "from a newer build"),
            _Recorded("UserText", "still readable"),
        ]);

        var (_, session, _) = _StartedAssistantOn(SessionCapabilities.ClaudeCli, sessionState, transcript);

        var entry = Assert.Single(session.Transcript);
        Assert.Equal("still readable", entry.Text);
    }

    /// <summary>Every new row is worth a snapshot — nothing else remembers what the operator saw across a restart.</summary>
    [Fact]
    public void ANewTranscriptRow_IsSaved_SoARestartCanReplayIt()
    {
        var transcript = Substitute.For<ISessionTranscriptStore>();
        var (_, session, _) = _StartedAssistantOn(SessionCapabilities.ClaudeCli, transcript: transcript);

        Dispatcher.UIThread.Invoke(() => session.Transcript.Add(
            new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "what is the status of AC-223")));

        transcript.Received().AppendAsync(
            AssistantSessionHost.AssistantPaneId,
            Arg.Is<TranscriptSnapshotEntry>(saved => saved.Text == "what is the status of AC-223"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// AC-955 criterion 10: a question card's ticked options and typed "Other" text are captured on save under an
    /// `answers` key, not just the bare question — otherwise a resumed conversation has nothing to redraw beyond
    /// an unanswered-looking card for a question the operator already answered.
    /// </summary>
    [Fact]
    public void AnAnsweredQuestionRow_SavesItsOptionsAndAnswer_NotOnlyTheBareQuestion()
    {
        var transcript = Substitute.For<ISessionTranscriptStore>();
        var (_, session, _) = _StartedAssistantOn(SessionCapabilities.ClaudeCli, transcript: transcript);

        const string inputJson = """{"questions":[{"question":"Which profile?","options":[{"label":"Core"},{"label":"All"}]}]}""";
        var prompts = AskUserQuestionViewModel.Parse(inputJson);
        prompts[0].Options[1].SelectCommand.Execute(null);
        prompts[0].IsAnswered = true;
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.Question, "Which profile?")
        {
            InputJson = inputJson,
            QuestionPrompts = prompts,
        };

        Dispatcher.UIThread.Invoke(() => session.Transcript.Add(entry));

        transcript.Received().AppendAsync(
            AssistantSessionHost.AssistantPaneId,
            Arg.Is<TranscriptSnapshotEntry>(saved => saved.InputJson != null
                && saved.InputJson!.Contains("\"answers\"", StringComparison.Ordinal)
                && saved.InputJson!.Contains("All", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// AC-955 criterion 10: a question card replays with its options and, if it was answered, its answer,
    /// read-only — the gap the grooming named: without this fix a beantwoorde kaart replayed as a blank
    /// Question row for a call the operator had already responded to.
    /// </summary>
    [Fact]
    public void ResumingByConversationId_ReplaysAnAnsweredQuestionCard_WithItsOptionsAndAnswerReadOnly()
    {
        var sessionState = Substitute.For<ISessionStateStore>();
        sessionState.TryLoadAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<SessionStateRecord>?>(_ =>
            [_StateFor(AssistantSessionHost.AssistantPaneId, "conv-1")]);

        const string savedInputJson = """
        {"questions":[{"question":"Which profile?","options":[{"label":"Core"},{"label":"All"}]}],
         "answers":{"Which profile?":{"options":["All"]}}}
        """;
        var transcript = Substitute.For<ISessionTranscriptStore>();
        transcript.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<IReadOnlyList<TranscriptSnapshotEntry>>(_ =>
            [_Recorded("Question", "Which profile?", savedInputJson)]);

        var (_, session, _) = _StartedAssistantOn(SessionCapabilities.ClaudeCli, sessionState, transcript);

        var entry = Assert.Single(session.Transcript);
        Assert.True(entry.HasQuestionPrompts);
        var prompt = Assert.Single(entry.QuestionPrompts!);
        Assert.True(prompt.IsAnswered);
        Assert.Equal("All", prompt.Answer);
        Assert.True(prompt.Options[1].IsSelected);
        Assert.False(prompt.Options[0].IsSelected);
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
    /// The mirror of the test above: a turn that fails once the operator is well into a resumed conversation is an
    /// ordinary failed reply, not a refused resume — the transcript already has more than the one row a genuine
    /// resume failure produces before anything else, so this must not tear the session down.
    /// </summary>
    [Fact]
    public void AFailedTurnLaterInAResumedConversation_DoesNotReadAsAnUnresolvableResume()
    {
        var sessionState = Substitute.For<ISessionStateStore>();
        sessionState.TryLoadAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<SessionStateRecord>?>(_ =>
            [_StateFor(AssistantSessionHost.AssistantPaneId, "conv-1")]);
        var transcript = Substitute.For<ISessionTranscriptStore>();
        transcript.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<IReadOnlyList<TranscriptSnapshotEntry>>(_ =>
            [_Recorded("UserText", "earlier message")]);

        var (host, first, _) = _StartedAssistantOn(SessionCapabilities.ClaudeCli, sessionState, transcript);

        // The operator actually asked something first — the row a real send always adds before its turn can run,
        // let alone fail. This is what the watcher retires on, harmlessly, before the failure below ever arrives.
        Dispatcher.UIThread.Invoke(() => first.Transcript.Add(
            new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "and what about AC-1")));

        Dispatcher.UIThread.Invoke(() => first.Apply(new TurnCompleted
        {
            SessionId = "conv-1",
            Subtype = "error_during_execution",
            Result = null,
            IsError = true,
            Errors = ["Something else went wrong"],
        }));

        // No replacement happens: give the (fire-and-forget, if it were wrongly triggered) recovery a moment, then
        // assert the original instance is still standing.
        Thread.Sleep(200);
        Assert.Same(first, host.Session);
    }

    /// <summary>
    /// Criterion 2 (AC-947): a launch that will not replay the saved rows into the new session — no resumable
    /// conversation id — archives the file that held them first, before the first new row can overwrite it.
    /// </summary>
    [Fact]
    public void StartingWithNoResumableConversation_ArchivesTheSavedTranscript()
    {
        var transcript = Substitute.For<ISessionTranscriptStore>();

        _StartedAssistantOn(SessionCapabilities.ClaudeCli, transcript: transcript);

        transcript.Received(1).ArchiveAsync(AssistantSessionHost.AssistantPaneId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClearConversation_ArchivesOnceAndLeavesTheClearDivider()
    {
        var transcript = Substitute.For<ISessionTranscriptStore>();
        var (host, first, _) = _StartedAssistantOn(SessionCapabilities.ClaudeCli, transcript: transcript);
        transcript.ClearReceivedCalls();

        var second = await Dispatcher.UIThread.InvokeAsync(() => host.ClearConversationAsync());

        Assert.NotNull(second);
        Assert.NotSame(first, second);
        var divider = Assert.Single(second!.Transcript, entry => entry.IsDivider);
        Assert.Equal("Conversation cleared — a new one starts here", divider.Text);
        await transcript.Received(1).ArchiveAsync(AssistantSessionHost.AssistantPaneId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void RequestedClear_WaitsForTheTurnAndIsIdempotent()
    {
        var (host, first, _) = _StartedAssistantOn(SessionCapabilities.ClaudeCli);
        Dispatcher.UIThread.Invoke(() => first.IsBusy = true);

        var firstRequest = Dispatcher.UIThread.Invoke(host.RequestConversationClear);
        var secondRequest = Dispatcher.UIThread.Invoke(host.RequestConversationClear);

        Assert.True(firstRequest.Ok);
        Assert.False(firstRequest.AlreadyPending);
        Assert.True(secondRequest.Ok);
        Assert.True(secondRequest.AlreadyPending);
        Assert.Same(first, host.Session);

        Dispatcher.UIThread.Invoke(() => first.IsBusy = false);

        Assert.NotSame(first, _ReplacementOf(host, first));
    }

    [Fact]
    public async Task RequestedClear_ExpiresWhenItsSessionIsReplaced()
    {
        var (host, first, _) = _StartedAssistantOn(SessionCapabilities.ClaudeCli);
        Dispatcher.UIThread.Invoke(() => first.IsBusy = true);
        Dispatcher.UIThread.Invoke(host.RequestConversationClear);

        var second = await Dispatcher.UIThread.InvokeAsync(() => host.RestartAsync());
        Assert.NotNull(second);
        Assert.NotSame(first, second);

        var requestOnReplacement = Dispatcher.UIThread.Invoke(host.RequestConversationClear);
        Assert.False(requestOnReplacement.AlreadyPending);
    }

    [Fact]
    public async Task ClearConversation_PreservesMemoryAndStateAndReloadsBothIntoTheNewInstruction()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"assistant-clear-{Guid.NewGuid():N}");
        var memoryPath = Path.Combine(directory, "assistant-memory.md");
        var statePath = Path.Combine(directory, "assistant-state.md");

        try
        {
            var memory = new AssistantMemoryFile(memoryPath, statePath);
            await memory.RememberAsync("The operator is called Raymond.");
            await memory.NoteCurrentStateAsync("We are implementing AC-1261.");
            var memoryBefore = await File.ReadAllBytesAsync(memoryPath);
            var stateBefore = await File.ReadAllBytesAsync(statePath);
            var launches = new List<IReadOnlyDictionary<string, string>?>();
            var driver = Substitute.For<ISessionDriver>();
            driver.Events.Returns(_EmptyEvents());
            driver.Capabilities.Returns(SessionCapabilities.ClaudeCli);
            driver.StartAsync(
                    Arg.Any<SessionProfile?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                    Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(), Arg.Any<SessionResume?>(),
                    Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    launches.Add(call.ArgAt<IReadOnlyDictionary<string, string>?>(6));
                    return Task.CompletedTask;
                });
            var factory = Substitute.For<ISessionDriverFactory>();
            factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
            var cockpit = await Dispatcher.UIThread.InvokeAsync(() => _CockpitWithSessionFactory(
                () => new SessionViewModel(new SessionManager(factory))));
            var host = await Dispatcher.UIThread.InvokeAsync(() => _Host(
                enabled: true, slot: _ConfiguredSlot(), cockpit: cockpit, memory: memory));

            await Dispatcher.UIThread.InvokeAsync(() => host.EnsureStartedAsync());
            launches.Clear();
            await Dispatcher.UIThread.InvokeAsync(() => host.ClearConversationAsync());

            Assert.Equal(memoryBefore, await File.ReadAllBytesAsync(memoryPath));
            Assert.Equal(stateBefore, await File.ReadAllBytesAsync(statePath));
            var instruction = Assert.Single(launches)![WellKnownPluginSessionOptions.AppendSystemPrompt];
            Assert.Contains(AssistantStandingInstruction.MemoryHeading, instruction, StringComparison.Ordinal);
            Assert.Contains("The operator is called Raymond.", instruction, StringComparison.Ordinal);
            Assert.Contains(AssistantStandingInstruction.CurrentStateHeading, instruction, StringComparison.Ordinal);
            Assert.Contains("We are implementing AC-1261.", instruction, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// The mirror of the test above: a launch that does replay the saved rows must not also archive them — the
    /// happy path keeps growing the one live file, unchanged from before AC-947.
    /// </summary>
    [Fact]
    public void ResumingByConversationId_DoesNotArchiveTheTranscript()
    {
        var sessionState = Substitute.For<ISessionStateStore>();
        sessionState.TryLoadAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<SessionStateRecord>?>(_ =>
            [_StateFor(AssistantSessionHost.AssistantPaneId, "conv-1")]);
        var transcript = Substitute.For<ISessionTranscriptStore>();

        _StartedAssistantOn(SessionCapabilities.ClaudeCli, sessionState, transcript);

        transcript.DidNotReceive().ArchiveAsync(AssistantSessionHost.AssistantPaneId, Arg.Any<CancellationToken>());
    }

    // A provider that vouches for compacting its own conversation — the one capability AC-664 turns on.
    private static SessionCapabilities _CompactingProvider() =>
        SessionCapabilities.ClaudeCli with { SupportsContextCompaction = true };

    // The hand-over runs fire-and-forget off a property change and tears the old runtime down on the way, which is a
    // real await — so the replacement lands a beat after the reading that triggered it, not inside it.
    private static SessionViewModel _ReplacementOf(AssistantSessionHost host, SessionViewModel previous)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (host.Session is null || ReferenceEquals(host.Session, previous))
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("The assistant was not handed over to a fresh session within 5s.");
            }

            Thread.Sleep(10);
        }

        return host.Session!;
    }

    private static (AssistantSessionHost Host, SessionViewModel Session, ISessionDriver Driver) _StartedAssistantOn(
        SessionCapabilities capabilities, ISessionStateStore? sessionState = null, ISessionTranscriptStore? transcript = null)
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
            () => new SessionViewModel(new SessionManager(factory), transcriptStore: transcript)));
        var host = Dispatcher.UIThread.Invoke(() => _Host(
            enabled: true, slot: _ConfiguredSlot(), cockpit: cockpit, sessionState: sessionState));

        var session = Dispatcher.UIThread.Invoke(() => host.EnsureStartedAsync().GetAwaiter().GetResult());
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

    [Fact]
    public void LaunchOptions_WithNothingEverRemembered_CarryNoMemoryHeading()
    {
        var options = AssistantSessionHost._LaunchOptions(_Profile(), replacesStandingInstruction: false, memory: "   ");

        Assert.DoesNotContain(
            AssistantStandingInstruction.MemoryHeading,
            options[WellKnownPluginSessionOptions.AppendSystemPrompt],
            StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchOptions_WithAnEmptyBox_AreTheBuiltInInstruction_WhicheverWayTheAdvancedOptionIsSet()
    {
        // Nothing written is nothing to replace: an operator who ticked the box and then cleared the text would
        // otherwise start an assistant with no instruction at all.
        var profile = _Profile() with { SystemPrompt = "   " };

        var options = AssistantSessionHost._LaunchOptions(profile, replacesStandingInstruction: true, memory: null);

        Assert.Equal(AssistantSystemPrompt.Default, options[WellKnownPluginSessionOptions.AppendSystemPrompt]);
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
        Dispatcher.UIThread.Invoke(() => host.Session = running);

        var kept = Dispatcher.UIThread.Invoke(() => host.EnsureStartedAsync().GetAwaiter().GetResult());
        Assert.Same(running, kept);
        Assert.Same(running, host.Session);

        Dispatcher.UIThread.Invoke(() => host.RestartAsync().GetAwaiter().GetResult());

        // Gone, and not quietly left in place looking reachable. (Nothing takes its place here: this cockpit has no
        // session factory, so the fresh start says so — which is the failure path, reported, rather than silence.)
        Assert.NotSame(running, host.Session);
        Assert.Equal(AssistantActivity.Unavailable, host.Activity);
        Assert.NotNull(host.UnavailableReason);
    }

    /// <summary>
    /// The restart re-reads the Assistant Profile, so a permission mode edited since the last launch is the one
    /// the new instance starts on. A restart that reused the profile it started with would tear the session down
    /// and bring it back on exactly the setting the operator restarted to get away from.
    /// </summary>
    [Fact]
    public void Restart_ReadsTheAssistantProfileAgain_SoASettingChangedSinceTheLastStartIsTheOneItStartsOn()
    {
        var profiles = Substitute.For<IAssistantProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ConfiguredSlot());
        var host = Dispatcher.UIThread.Invoke(() => _Host(enabled: true, slot: _ConfiguredSlot(), profiles: profiles));
        Dispatcher.UIThread.Invoke(() => host.Session = new _RunningSession());

        Dispatcher.UIThread.Invoke(() => host.RestartAsync().GetAwaiter().GetResult());

        profiles.Received().LoadAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A restart is not a reset: it picks the recorded conversation back up. The state store's last record for the
    /// assistant's pane is what names it — the same mechanism every restored session uses (AC-409/AC-410) — and
    /// the restart runs the one start path that reads it.
    /// </summary>
    [Fact]
    public void TheAssistantsResume_IsTheConversationItsPaneLastRecorded_NotAFreshOne()
    {
        var state = Substitute.For<ISessionStateStore>();
        state.TryLoadAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<SessionStateRecord>?>(_ =>
        [
            _StateFor("some-other-pane", "conv-elsewhere"),
            _StateFor(AssistantSessionHost.AssistantPaneId, "conv-assistant"),
        ]);
        var host = Dispatcher.UIThread.Invoke(() => _Host(enabled: true, slot: _ConfiguredSlot(), sessionState: state));

        var resume = Dispatcher.UIThread.Invoke(() => host._ResolveResumeAsync(default).GetAwaiter().GetResult());

        Assert.Equal(SessionResumeMode.BySessionId, resume.Mode);
        Assert.Equal("conv-assistant", resume.SessionId);
    }

    /// <summary>
    /// AC-1089 criterion 4: startup compaction drops state for every pane its roster does not name (AC-410), and the
    /// assistant owns no workspace pane — so each start erased its saved conversation id before the resume above
    /// could read it, and no <c>--resume</c> was ever sent. Drives the real roster and the real store the way
    /// <c>Program.ReconcileWorktreesAndCompactStateAsync</c> does: a substituted store is exactly what hid this,
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

    [Fact]
    public void TheAssistantsResume_WithNothingRecordedYet_IsAFreshConversation()
    {
        var host = Dispatcher.UIThread.Invoke(() => _Host(enabled: true, slot: _ConfiguredSlot()));

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
        var broker = Substitute.For<IConsentBroker>();
        var prompt = new ConsentPrompt(
            Guid.NewGuid(),
            new ConsentRequest(
                "Run a command",
                "rm -rf /",
                new ConsentSource(AssistantSessionHost.AssistantPaneId, null, "Terminal MCP"),
                "scope",
                ConsentRisk.Dangerous),
            CanRemember: false);

        var host = Dispatcher.UIThread.Invoke(() => _Host(enabled: true, slot: _ConfiguredSlot()));
        Dispatcher.UIThread.Invoke(() => host.Session = new _RunningSession
        {
            PendingConsent = new ConsentPromptViewModel(prompt, broker),
        });

        Dispatcher.UIThread.Invoke(() => host.RestartAsync().GetAwaiter().GetResult());

        broker.Received(1).Respond(prompt.Id, ConsentOutcome.Denied, false);
    }

    /// <summary>A session whose runtime is up, which is the state that cannot be produced without a real child process.</summary>
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
        IAssistantMemory? memory = null)
    {
        var settings = Substitute.For<IAssistantSettingsStore>();
        settings.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new AssistantSettings { IsEnabled = enabled, ReadingLevel = readingLevel, SpeakReplies = speakReplies });

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

        return new AssistantSessionHost(
            cockpit ?? new CockpitViewModel(), settings, profiles, sessionState, sessionStateRecorder,
            catalog ?? _Catalog(), memory ?? Substitute.For<IAssistantMemory>(),
            NullLogger<AssistantSessionHost>.Instance);
    }

    private static ISessionTranscriptStore _EmptyTranscriptStore()
    {
        var store = Substitute.For<ISessionTranscriptStore>();
        store.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<IReadOnlyList<TranscriptSnapshotEntry>>([]);
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
