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
/// AC-1305: the Simple stand shows one conversation, so a session asking for consent from outside that column
/// has to say so itself. The notification names the session and takes you there; it never answers for you.
/// </summary>
/// <remarks>
/// Criterion 3 (the session carries the hand whether or not a notification was shown, and loses it the moment the
/// consent is answered from anywhere) needs nothing here: it rests on <c>SessionPanelViewModel.PendingConsent</c>,
/// which lives on the session and not on any notification, and
/// <c>AssistantConsentRoutingTests.AfterTheFirstConsentIsAnswered_TheNextOneIsShown_RatherThanSilentlyDenied</c>
/// already pins that a prompt closed through the broker clears it on the pane it was shown on.
/// </remarks>
[Collection("avalonia")]
public class Ac1305SimpleConsentAlertTests
{
    /// <summary>
    /// Criterion 1, both halves. The assistant is what this stand's column draws, so its own request needs no
    /// notification — the question is on the screen already. Any other session's does, by name.
    /// </summary>
    /// <remarks>
    /// The toast assertions are the second half of the same rule. <c>_OnConsentPromptOpened</c> raises the panels
    /// stand's own "Consent needed" toast, and that overlay is declared outside <c>PanelsRoot</c>, so without the
    /// suppression the Simple stand would show two notifications for one request — one of them offering a Review
    /// button that moves a selection this stand does not use, and timing out after five seconds.
    /// </remarks>
    [Fact]
    public void TheSessionOutOfViewIsNamed_AndTheOneInViewRaisesNothingAtAll()
    {
        var broker = Substitute.For<IConsentBroker>();
        var forTheAssistant = _Prompt(AssistantIdentity.PaneId);

        var cockpit = Dispatcher.UIThread.Invoke(() =>
        {
            var built = _Cockpit(broker);
            built.SimpleView = true;
            built.CreateAssistantSession(AssistantIdentity.PaneId);

            broker.PromptOpened += Raise.Event<EventHandler<ConsentPrompt>>(broker, forTheAssistant);
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(built.SimpleConsentAlerts);
            Assert.Empty(built.Toasts);

            var elsewhere = new SessionViewModel { Title = "Deploy" };
            built.Sessions.Add(elsewhere);
            broker.PromptOpened += Raise.Event<EventHandler<ConsentPrompt>>(broker, _Prompt(elsewhere.PaneId));
            Dispatcher.UIThread.RunJobs();
            return built;
        });

        var alert = Assert.Single(cockpit.SimpleConsentAlerts);
        Assert.Equal("Deploy", alert.Title);
        Assert.Equal("Delete the staging namespace", alert.PendingConsent!.Title);
        Assert.Empty(cockpit.Toasts);
    }

    /// <summary>
    /// Criterion 2(b), and the failure this whole ticket is watched for: an <c>Ignore</c> that lands as a refusal
    /// is a command nobody refused, and there is no screen in the cockpit on which that is visible. Ignoring takes
    /// the notification away and does nothing else — the consent stays open, waiting for a real answer.
    /// </summary>
    [Fact]
    public void IgnoreTakesTheNotificationAwayAndLeavesTheConsentStandingOpen()
    {
        var broker = Substitute.For<IConsentBroker>();
        var (cockpit, asking, prompt) = _WithAConsentOutOfView(broker);

        Dispatcher.UIThread.Invoke(() =>
            cockpit.IgnoreConsentAlertCommand.Execute(cockpit.SimpleConsentAlerts[0]));

        Assert.Empty(cockpit.SimpleConsentAlerts);
        Assert.NotNull(asking.PendingConsent);
        Assert.Equal(prompt.Id, asking.PendingConsent!.Id);
        broker.DidNotReceive().Respond(prompt.Id, Arg.Any<ConsentOutcome>(), Arg.Any<bool>());
    }

    /// <summary>
    /// Criterion 2(a): <c>Go there</c> picks the asking session and answers nothing on the way.
    /// </summary>
    /// <remarks>
    /// AC-1303 coupled the conversation column to that pick, so the notification now goes down with it — the card
    /// is on screen, and naming a session you are already looking at is noise rather than a safety net. Until then
    /// this asserted the opposite, deliberately: taking it down while the column still drew the assistant would
    /// have left the card nowhere at all. The consent itself is still untouched, which is the rest of 2(a).
    /// </remarks>
    [Fact]
    public void GoThereChoosesTheAskingSession_TakesItsNotificationDown_AndLeavesItsQuestionUnanswered()
    {
        var broker = Substitute.For<IConsentBroker>();
        var (cockpit, asking, prompt) = _WithAConsentOutOfView(broker);

        Dispatcher.UIThread.Invoke(() =>
            cockpit.GoToConsentAlertCommand.Execute(cockpit.SimpleConsentAlerts[0]));

        Assert.Same(asking, cockpit.SimpleSelectedSession);
        Assert.Empty(cockpit.SimpleConsentAlerts);
        Assert.NotNull(asking.PendingConsent);
        broker.DidNotReceive().Respond(prompt.Id, Arg.Any<ConsentOutcome>(), Arg.Any<bool>());
    }

    /// <summary>
    /// Criterion 4, which the prototype never drew: two sessions asking at once are both on screen, and answering
    /// one leaves the other standing. A single notification slot would lose one of the two silently.
    /// </summary>
    [Fact]
    public void TwoSessionsAskingAtOnceAreBothShown_AndAnsweringOneLeavesTheOther()
    {
        var broker = Substitute.For<IConsentBroker>();
        SessionPanelViewModel second = null!;
        ConsentPrompt first = null!;

        var cockpit = Dispatcher.UIThread.Invoke(() =>
        {
            var built = _Cockpit(broker);
            built.SimpleView = true;
            built.CreateAssistantSession(AssistantIdentity.PaneId);

            var one = new SessionViewModel { Title = "Deploy" };
            second = new SessionViewModel { Title = "invoices" };
            built.Sessions.Add(one);
            built.Sessions.Add(second);

            first = _Prompt(one.PaneId);
            broker.PromptOpened += Raise.Event<EventHandler<ConsentPrompt>>(broker, first);
            broker.PromptOpened += Raise.Event<EventHandler<ConsentPrompt>>(broker, _Prompt(second.PaneId));
            Dispatcher.UIThread.RunJobs();
            return built;
        });

        Assert.Equal(2, cockpit.SimpleConsentAlerts.Count);

        // One answered — through the broker's close side, which is what a click on either card's buttons runs.
        Dispatcher.UIThread.Invoke(() =>
        {
            broker.PromptClosed += Raise.Event<EventHandler<Guid>>(broker, first.Id);
            Dispatcher.UIThread.RunJobs();
        });

        Assert.Same(second, Assert.Single(cockpit.SimpleConsentAlerts));
    }

    // A Simple stand with a consent open on a session that is not the one its column draws — the state criterion
    // 2's two buttons are pressed from.
    private static (CockpitViewModel Cockpit, SessionPanelViewModel Asking, ConsentPrompt Prompt) _WithAConsentOutOfView(
        IConsentBroker broker)
    {
        SessionPanelViewModel asking = null!;
        ConsentPrompt prompt = null!;

        var cockpit = Dispatcher.UIThread.Invoke(() =>
        {
            var built = _Cockpit(broker);
            built.SimpleView = true;
            built.CreateAssistantSession(AssistantIdentity.PaneId);

            asking = new SessionViewModel { Title = "Deploy" };
            built.Sessions.Add(asking);

            prompt = _Prompt(asking.PaneId);
            broker.PromptOpened += Raise.Event<EventHandler<ConsentPrompt>>(broker, prompt);
            Dispatcher.UIThread.RunJobs();
            return built;
        });

        Assert.Single(cockpit.SimpleConsentAlerts);
        return (cockpit, asking, prompt);
    }

    private static ConsentPrompt _Prompt(string paneId) => new(
        Guid.NewGuid(),
        new ConsentRequest(
            "Delete the staging namespace",
            "kubectl delete namespace staging",
            new ConsentSource(paneId, PluginId: "cockpit-k8s", Label: "Kubernetes"),
            Scope: "cluster",
            Risk: ConsentRisk.Dangerous),
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
