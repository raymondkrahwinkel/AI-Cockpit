using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using NSubstitute;
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

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1301: the Simple stand — the setting that decides which stand the cockpit opens in, the in-view
/// switch that deliberately does not touch it, and the selection each stand keeps of its own.
/// </summary>
[Collection("avalonia")]
public class Ac1301SimpleViewStandTests
{
    // Criterion 1: the setting decides the stand the app opens in, and the switch decides only what is on
    // screen now — so flipping the switch and restarting comes back to what the setting says.
    [Fact]
    public void TheSettingSeedsTheStandAndTheSwitchLeavesItAlone()
    {
        var layout = _LayoutStore(new LayoutSettings { OpenInSimpleView = true });
        var cockpit = _Cockpit(layout);

        Assert.True(cockpit.SimpleView);
        Assert.True(cockpit.OpenInSimpleView);

        cockpit.ShowPanelsViewCommand.Execute(null);

        Assert.False(cockpit.SimpleView);
        Assert.True(cockpit.OpenInSimpleView);
        layout.DidNotReceive().SaveAsync(Arg.Any<LayoutSettings>());
    }

    // Criterion 3: the save is awaited, not started and let go. A handler that returned before the write
    // finished would let the window close on a value that never reached disk — and would still say "Saved".
    [Fact]
    public async Task SavingTheSettingWaitsForTheWriteBeforeSayingItIsSaved()
    {
        var pending = new TaskCompletionSource();
        var layout = _LayoutStore(new LayoutSettings());
        layout.SaveAsync(Arg.Any<LayoutSettings>()).Returns(_ => pending.Task);

        var cockpit = _Cockpit(layout);
        cockpit.OpenInSimpleView = true;

        var save = cockpit.SaveLayoutSettingsCommand.ExecuteAsync(null);

        Assert.False(save.IsCompleted);
        Assert.NotEqual("Saved", cockpit.LayoutSettingsStatus);

        pending.SetResult();
        await save;

        Assert.Equal("Saved", cockpit.LayoutSettingsStatus);
        await layout.Received().SaveAsync(Arg.Is<LayoutSettings>(settings => settings.OpenInSimpleView));
    }

    // Criterion 4: each stand keeps its own selection, so a switch there and back returns to what you were
    // looking at in that stand rather than to one shared choice.
    [Fact]
    public void EachStandKeepsItsOwnSelection()
    {
        var cockpit = _Cockpit(_LayoutStore(new LayoutSettings()));
        var inPanels = new SessionViewModel();
        var inSimple = new SessionViewModel();

        cockpit.SelectedSession = inPanels;
        cockpit.ShowSimpleViewCommand.Execute(null);
        cockpit.SimpleSelectedSession = inSimple;
        cockpit.ShowPanelsViewCommand.Execute(null);

        Assert.Same(inPanels, cockpit.SelectedSession);

        cockpit.ShowSimpleViewCommand.Execute(null);

        Assert.Same(inSimple, cockpit.SimpleSelectedSession);
        Assert.Same(inPanels, cockpit.SelectedSession);
    }

    // The switch is a pair of segments, not two toggles: clicking the stand that is already on screen changes
    // nothing — not the stand, and not the segment's selection either. A ToggleButton flips its own IsChecked on
    // every click regardless of the binding, so the same click used to blank the selected segment.
    [Fact]
    public void ClickingTheStandAlreadyOnScreen_LeavesTheSelectionStanding() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = _Cockpit(_LayoutStore(new LayoutSettings { OpenInSimpleView = true }));
        var window = new Window { Width = 200, Height = 100, Content = new ViewModeSwitch { DataContext = cockpit } };
        window.Show();
        try
        {
            var simple = window.GetVisualDescendants().OfType<ToggleButton>().Single(b => Equals(b.Content, "Simple"));
            var panels = window.GetVisualDescendants().OfType<ToggleButton>().Single(b => Equals(b.Content, "Panels"));
            Assert.True(simple.IsChecked);
            Assert.False(panels.IsChecked);

            _Click(window, simple);

            Assert.True(cockpit.SimpleView);
            Assert.True(simple.IsChecked, "clicking the active segment must not blank it");
            Assert.False(panels.IsChecked);

            _Click(window, panels);
            _Click(window, panels);

            Assert.False(cockpit.SimpleView);
            Assert.True(panels.IsChecked, "clicking the active segment must not blank it");
            Assert.False(simple.IsChecked);
        }
        finally
        {
            window.Close();
        }
    });

    private static void _Click(Window window, Control control)
    {
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
    }

    private static ILayoutSettingsStore _LayoutStore(LayoutSettings settings)
    {
        var layout = Substitute.For<ILayoutSettingsStore>();
        layout.LoadAsync().Returns(settings);
        return layout;
    }

    private static CockpitViewModel _Cockpit(ILayoutSettingsStore layout)
    {
        var notifications = Substitute.For<INotificationSettingsStore>();
        notifications.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplay = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplay.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehavior = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehavior.LoadAsync().Returns(new SessionBehaviorSettings());
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
            terminal);
    }
}
