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
