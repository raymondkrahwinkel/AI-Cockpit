using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Consent;
using Cockpit.Plugins.Abstractions.Consent;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// A consent request naming the assistant's pane reaches the assistant (AC-545, found in the live test).
/// </summary>
/// <remarks>
/// This is the cockpit's own gate (#AC-47) — a k8s, docker or terminal tool asking the host — and not the SDK's
/// Allow/Deny row in the transcript. The two look alike on screen and are answered in completely different places.
/// <para>
/// The defect this pins was silent and total: <c>CockpitViewModel</c> routes a prompt with
/// <c>FindSession(paneId)</c>, the assistant is in neither <c>Sessions</c> nor the embedded table by construction,
/// so every one of its consents fell into the "nowhere to show it" branch and was denied on the spot. The operator
/// saw an Allowed tool row (the SDK's permission, one layer up) and a result saying the operator did not approve —
/// with nothing having been put in front of them to approve. Phase 1's lesson, one gate further along.
/// </para>
/// </remarks>
[Collection("avalonia")]
public class AssistantConsentRoutingTests
{
    [Fact]
    public void AConsentNamingTheAssistantsPane_IsShownOnTheAssistant_NotDeniedOutright()
    {
        var broker = Substitute.For<IConsentBroker>();
        var prompt = _Prompt(AssistantIdentity.PaneId);

        var assistant = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = _Cockpit(broker);
            var session = cockpit.CreateAssistantSession(AssistantIdentity.PaneId);
            broker.PromptOpened += Raise.Event<EventHandler<ConsentPrompt>>(broker, prompt);

            // The routing hops to the UI thread with Post — a prompt can be raised from a Kestrel request thread —
            // so the queue has to be drained before there is anything to assert about.
            Dispatcher.UIThread.RunJobs();
            return session;
        });

        // Shown, and not answered on the operator's behalf: the banner is the gate, and only a click resolves it.
        Assert.NotNull(assistant);
        Assert.NotNull(assistant!.PendingConsent);
        Assert.Equal(prompt.Id, assistant.PendingConsent!.Id);
        broker.DidNotReceive().Respond(prompt.Id, Arg.Any<ConsentOutcome>(), Arg.Any<bool>());
    }

    /// <summary>
    /// Answering one card lets the next one be shown. The open side learned to find the assistant and the close side
    /// did not, so the first card's <c>PendingConsent</c> was never cleared — and the one-banner-per-pane rule then
    /// denied every later request on that pane without putting anything on screen. With <c>send_message</c> and
    /// <c>send_prompt</c> asking on every call by design, that is the second one, silently, for the life of the
    /// process; restarting the assistant was the only way out.
    /// </summary>
    [Fact]
    public void AfterTheFirstConsentIsAnswered_TheNextOneIsShown_RatherThanSilentlyDenied()
    {
        var broker = Substitute.For<IConsentBroker>();
        var first = _Prompt(AssistantIdentity.PaneId);
        var second = _Prompt(AssistantIdentity.PaneId);

        var assistant = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = _Cockpit(broker);
            var session = cockpit.CreateAssistantSession(AssistantIdentity.PaneId);

            broker.PromptOpened += Raise.Event<EventHandler<ConsentPrompt>>(broker, first);
            Dispatcher.UIThread.RunJobs();

            // What the operator's click does: ConsentService.Respond takes the banner down by raising this.
            broker.PromptClosed += Raise.Event<EventHandler<Guid>>(broker, first.Id);
            Dispatcher.UIThread.RunJobs();

            // Asserted inside the invoke, before the second prompt can hide a card that was never cleared.
            Assert.Null(session!.PendingConsent);

            broker.PromptOpened += Raise.Event<EventHandler<ConsentPrompt>>(broker, second);
            Dispatcher.UIThread.RunJobs();
            return session;
        });

        Assert.NotNull(assistant.PendingConsent);
        Assert.Equal(second.Id, assistant.PendingConsent!.Id);
        broker.DidNotReceive().Respond(second.Id, Arg.Any<ConsentOutcome>(), Arg.Any<bool>());
    }

    [Fact]
    public void AConsentNamingAPaneThatDoesNotExist_IsStillDenied_RatherThanLeftHanging()
    {
        // The other half of the branch. Without it this file would pass on a cockpit that shows every prompt to the
        // assistant, including one belonging to a pane that has since closed — and the fail-closed rule matters more
        // than the routing does.
        var broker = Substitute.For<IConsentBroker>();
        var prompt = _Prompt("pane-that-went-away");

        Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = _Cockpit(broker);
            cockpit.CreateAssistantSession(AssistantIdentity.PaneId);
            broker.PromptOpened += Raise.Event<EventHandler<ConsentPrompt>>(broker, prompt);

            // The routing hops to the UI thread with Post — a prompt can be raised from a Kestrel request thread —
            // so the queue has to be drained before there is anything to assert about.
            Dispatcher.UIThread.RunJobs();
        });

        broker.Received().Respond(prompt.Id, ConsentOutcome.Denied, false);
    }

    /// <summary>
    /// AC-711: the whole Assistant pop-out window going grey and unrecoverable, with no error and no way back
    /// short of an app restart. <c>AssistantIdentity.PaneId</c> is a reserved identity a replacement instance
    /// (restart, AC-596's context hand-over, AC-602's idle stop) adopts too — so a prompt whose routing was still
    /// queued when the live instance got replaced used to land on that unrelated successor instead of being
    /// denied. Nothing left alive would ever answer it: AC-47's full-pane scrim (<c>ConsentBannerHost</c>) stayed
    /// up over the pop-out for good, on a freshly started conversation that had nothing to do with the request.
    /// </summary>
    [Fact]
    public void AConsentForTheAssistant_QueuedWhileItIsBeingReplaced_IsDenied_NotOrphanedOnTheReplacement()
    {
        var broker = Substitute.For<IConsentBroker>();
        var prompt = _Prompt(AssistantIdentity.PaneId);

        var (oldSession, newSession) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = _Cockpit(broker);
            var oldSession = cockpit.CreateAssistantSession(AssistantIdentity.PaneId);

            // Opened while `oldSession` is still the live instance — routing is only queued, not run yet.
            broker.PromptOpened += Raise.Event<EventHandler<ConsentPrompt>>(broker, prompt);

            // The instance is replaced before that queued routing gets a turn — exactly what
            // AssistantSessionHost._StartOrReplaceAsync does around a restart, a hand-over or an idle stop.
            cockpit.ReleaseAssistantSession(oldSession!);
            var newSession = cockpit.CreateAssistantSession(AssistantIdentity.PaneId);

            Dispatcher.UIThread.RunJobs();
            return (oldSession, newSession);
        });

        Assert.Null(oldSession!.PendingConsent);
        Assert.Null(newSession!.PendingConsent);
        broker.Received(1).Respond(prompt.Id, ConsentOutcome.Denied, false);
    }

    private static ConsentPrompt _Prompt(string paneId) => new(
        Guid.NewGuid(),
        new ConsentRequest(
            "Read pods",
            "kubectl get pods",
            new ConsentSource(paneId, PluginId: "cockpit-k8s", Label: "Kubernetes"),
            Scope: "cluster",
            Risk: ConsentRisk.LowRisk),
        CanRemember: false);

    private static CockpitViewModel _Cockpit(IConsentBroker broker)
    {
        var notifications = Substitute.For<INotificationSettingsStore>();
        notifications.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplay = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplay.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehavior = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehavior.LoadAsync().Returns(new SessionBehaviorSettings());
        var layout = Substitute.For<ILayoutSettingsStore>();
        layout.LoadAsync().Returns(new LayoutSettings());
        var voice = Substitute.For<IVoiceSettingsStore>();
        voice.LoadAsync().Returns(new VoiceSettings());
        var terminal = Substitute.For<ITerminalSettingsStore>();
        terminal.LoadAsync().Returns(new TerminalSettings());

        return new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            Substitute.For<ISessionDialogService>(),
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notifications,
            transcriptDisplay,
            sessionBehavior,
            layout,
            voice,
            terminal,
            consentBroker: broker);
    }
}
