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

// AC-1305: criterion 3 needs no test here — PendingConsent lives on the session, and AssistantConsentRoutingTests pins it.
[Collection("avalonia")]
public class Ac1305SimpleConsentAlertTests
{
    // The panels stand's consent toast is declared outside PanelsRoot; unsuppressed, one request would show two notifications.
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

    // AC-1303 moved the conversation column with the pick, so the notification goes down with it: a visible session needs no name.
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
