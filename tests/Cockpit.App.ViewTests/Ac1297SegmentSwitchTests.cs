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
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using NSubstitute;

namespace Cockpit.App.ViewTests;

// AC-1297: measured on the whole CockpitView — hosted alone, the same-named RadioButton groups never blanked each other.
[Collection("avalonia")]
public sealed class Ac1297SegmentSwitchTests
{
    // Criterion 1 and 2: on entry, the stand you are in is the one segment that is on. Only Panels carries the
    // switch since AC-1527; Simple's way out is More options.
    [Fact]
    public void OnEntry_TheStandOnScreenIsTheOnlySegmentOn() => HeadlessAvalonia.Run(() =>
    {
        var window = _Shown(_Cockpit(new LayoutSettings { OpenInSimpleView = false }));
        try
        {
            var (simpleButton, panelsButton) = _VisibleViewModeSegments(window);

            Assert.False(_On(simpleButton));
            Assert.True(_On(panelsButton));
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
