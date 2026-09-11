using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.Projects;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Core.Workspaces;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1297: the segmented switches — Simple/Panels and the Projects layout — show the selection the view model
/// holds, and nothing else. Measured against the whole <see cref="CockpitView"/> on purpose: the switch sits in it
/// twice (one per stand), and the regression this guards against (e5e7b027) was invisible on a switch hosted alone
/// — two RadioButton groups of the same name blanked each other across the visible and the hidden copy, so the
/// stand you entered in showed no selection at all.
/// </summary>
[Collection("avalonia")]
public sealed class Ac1297SegmentSwitchTests
{
    // Criterion 1 and 2: on entry, the stand you are in is the one segment that is on — in either stand, in the
    // copy of the switch that is actually on screen.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnEntry_TheStandOnScreenIsTheOnlySegmentOn(bool simple) => HeadlessAvalonia.Run(() =>
    {
        var window = _Shown(_Cockpit(new LayoutSettings { OpenInSimpleView = simple }));
        try
        {
            var (simpleButton, panelsButton) = _VisibleViewModeSegments(window);

            Assert.Equal(simple, _On(simpleButton));
            Assert.Equal(!simple, _On(panelsButton));
        }
        finally
        {
            window.Close();
        }
    });

    // Criterion 3: clicking the stand already on screen changes nothing — not the stand, and not the selection.
    // This is what e5e7b027 fixed for a ToggleButton, and it must hold for whatever the segment is made of.
    [Fact]
    public void ClickingTheStandAlreadyOnScreen_LeavesTheSelectionStanding() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = _Cockpit(new LayoutSettings { OpenInSimpleView = false });
        var window = _Shown(cockpit);
        try
        {
            var (_, panels) = _VisibleViewModeSegments(window);
            _Click(window, panels);
            _Click(window, panels);

            Assert.False(cockpit.SimpleView);
            var (simpleAfter, panelsAfter) = _VisibleViewModeSegments(window);
            Assert.True(_On(panelsAfter), "clicking the active segment must not blank it");
            Assert.False(_On(simpleAfter));

            // And across the stands: the copy on screen changes with the stand, and its selection follows.
            _Click(window, simpleAfter);
            window.UpdateLayout();

            Assert.True(cockpit.SimpleView);
            var (simpleInSimple, panelsInSimple) = _VisibleViewModeSegments(window);
            Assert.True(_On(simpleInSimple));
            Assert.False(_On(panelsInSimple));
        }
        finally
        {
            window.Close();
        }
    });

    // The Projects layout switch is the same form: on entry the persisted layout is the one segment on, and
    // clicking it again leaves it on.
    [Fact]
    public async Task ProjectsLayoutSwitch_ShowsThePersistedLayout_AndKeepsItWhenClickedAgain() =>
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var store = Substitute.For<IProjectStore>();
            store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings
            {
                Projects = [Project.Create("Cockpit") with { Category = "Privé" }],
                CategoryOrder = ["Privé"],
            });
            var projects = new ProjectsViewModel(store, Substitute.For<ISessionDialogService>());
            await projects.LoadAsync();
            projects.LayoutMode = ProjectsLayoutMode.List;

            var cockpit = _Cockpit(new LayoutSettings(), projects);
            await cockpit.Workspaces.OpenWorkspaceAsync(WorkspaceType.Projects.Id);
            var window = _Shown(cockpit);
            try
            {
                Assert.False(_On(_ProjectsSegment(window, "Cards")));
                Assert.True(_On(_ProjectsSegment(window, "List")));

                _Click(window, _ProjectsSegment(window, "List"));

                Assert.Equal(ProjectsLayoutMode.List, cockpit.Projects.LayoutMode);
                Assert.True(_On(_ProjectsSegment(window, "List")), "clicking the active segment must not blank it");
                Assert.False(_On(_ProjectsSegment(window, "Cards")));
            }
            finally
            {
                window.Close();
            }
        });

    private static Window _Shown(CockpitViewModel cockpit)
    {
        var window = new Window { Width = 1100, Height = 760, Content = new CockpitView { DataContext = cockpit } };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    // The copy of the switch that is on screen: the other stand's copy is not materialised, so it has no buttons.
    private static (Button Simple, Button Panels) _VisibleViewModeSegments(Window window)
    {
        var onScreen = window.GetVisualDescendants().OfType<ViewModeSwitch>()
            .Single(s => s.IsEffectivelyVisible && s.GetVisualDescendants().OfType<Button>().Any());
        var buttons = onScreen.GetVisualDescendants().OfType<Button>().ToList();
        return (buttons.Single(b => Equals(b.Content, "Simple")), buttons.Single(b => Equals(b.Content, "Panels")));
    }

    // The selection as the theme draws it: `Button.Segment.on` is the raised segment, and nothing else marks it.
    private static bool _On(Button segment) => segment.Classes.Contains("on");

    private static Button _ProjectsSegment(Window window, string mode) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("Segment") && Equals(b.CommandParameter, mode));

    private static void _Click(Window window, Control control)
    {
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
    }

    private static CockpitViewModel _Cockpit(LayoutSettings layoutSettings, ProjectsViewModel? projects = null)
    {
        var notifications = Substitute.For<INotificationSettingsStore>();
        notifications.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplay = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplay.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehavior = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehavior.LoadAsync().Returns(new SessionBehaviorSettings());
        var layout = Substitute.For<ILayoutSettingsStore>();
        layout.LoadAsync().Returns(layoutSettings);
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
            projects: projects);
    }
}
