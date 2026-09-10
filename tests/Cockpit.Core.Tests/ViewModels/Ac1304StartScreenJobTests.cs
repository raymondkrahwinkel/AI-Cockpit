using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-1304 criterion 3: what a job on the start screen does. It is the same quick start the card's own Start
/// already used (AC-164), carrying the prompt — and the promise printed above the cards, that nothing is sent
/// until Send is pressed, is what the text sitting unsent in the box is.
/// </summary>
/// <remarks>
/// <c>StartProjectJobCommand</c>, the Projects workspace's own answer for a job, keeps the dialog it has offered
/// since AC-491 and is unchanged — <c>CockpitViewModelProjectStartTests</c> still holds it to that. Which of the
/// two a surface wants is the surface's to say (<c>ProjectCardView.JobCommand</c>).
/// </remarks>
public class Ac1304StartScreenJobTests
{
    [Fact]
    public async Task StartingAJob_OpensNoDialog_AndLeavesItsPromptSittingInTheBox()
    {
        var (vm, dialogs) = _Cockpit();
        var job = new ProjectJob("Process this month's invoices", "changes nothing · reports only");
        var project = Project.Create("Invoices") with { DefaultProfileLabel = "work", Jobs = [job] };

        await vm.StartProjectJobNowCommand.ExecuteAsync(new ProjectJobChoice(project, job));

        await dialogs.DidNotReceive().ShowNewSessionDialogAsync(
            Arg.Any<NewSessionPrefill?>(), Arg.Any<bool>(), Arg.Any<Project?>());
        var session = Assert.IsType<SessionViewModel>(Assert.Single(vm.Sessions));
        Assert.Equal(job.Prompt, session.InputText);

        // Nothing sent: the prompt is in the box and nowhere in the conversation. Also what puts it in the Simple
        // stand's column, without which the click would open a session that stand never shows (criterion 2).
        Assert.DoesNotContain(session.Transcript, entry => entry.Text.Contains(job.Prompt, StringComparison.Ordinal));
        Assert.Same(session, vm.SimpleSelectedSession);
    }

    [Fact]
    public async Task StartingAProjectWithNoJob_OpensTheSameSessionWithAnEmptyBox()
    {
        var (vm, dialogs) = _Cockpit();
        var project = Project.Create("Invoices") with { DefaultProfileLabel = "work" };

        await vm.StartProjectSessionCommand.ExecuteAsync(project);

        // Criterion 3(b): "Something else — start empty" is this same start without a prompt, so nothing may be
        // placed in the box — not the project's name, not an empty string typed into it.
        await dialogs.DidNotReceive().ShowNewSessionDialogAsync(
            Arg.Any<NewSessionPrefill?>(), Arg.Any<bool>(), Arg.Any<Project?>());
        var session = Assert.IsType<SessionViewModel>(Assert.Single(vm.Sessions));
        Assert.Equal(string.Empty, session.InputText);
    }

    private static (CockpitViewModel Vm, ISessionDialogService Dialogs) _Cockpit()
    {
        // An SDK profile, because "the prompt is in the box" is a claim about a box: a TTY session has a terminal
        // and answers `InjectText` by writing into the pty, which is a different promise on a different surface.
        var profile = new SessionProfile("work", new ClaudeConfig(@"C:\fake\.claude"))
        {
            DefaultKind = ProfileSessionKind.Sdk,
        };
        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns([profile]);
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([]);
        var quickStart = new ProjectQuickStart(
            profiles, catalog, Substitute.For<ITtySessionProviderResolver>(), new ProjectMemorySourceRegistry());

        var notifications = Substitute.For<INotificationSettingsStore>();
        notifications.LoadAsync().Returns(new NotificationSettings());
        var transcripts = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcripts.LoadAsync().Returns(new TranscriptDisplaySettings());
        var behavior = Substitute.For<ISessionBehaviorSettingsStore>();
        behavior.LoadAsync().Returns(new SessionBehaviorSettings());
        var layout = Substitute.For<ILayoutSettingsStore>();
        layout.LoadAsync().Returns(new LayoutSettings());
        var voice = Substitute.For<IVoiceSettingsStore>();
        voice.LoadAsync().Returns(new VoiceSettings());
        var terminals = Substitute.For<ITerminalSettingsStore>();
        terminals.LoadAsync().Returns(new TerminalSettings());

        var dialogs = Substitute.For<ISessionDialogService>();
        var vm = new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            dialogs,
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notifications,
            transcripts,
            behavior,
            layout,
            voice,
            terminals,
            projectQuickStart: quickStart);

        return (vm, dialogs);
    }
}
