using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// Geometry the composer's XAML claims but no viewmodel test can see. Both findings here came out of Raymond's
/// live test of the SDK-view batch (2026-07-31) and neither was visible to the suite at the time: the row's
/// controls are all bottom-anchored, so a height difference reads as a staggered top edge rather than as
/// anything a binding assertion would catch, and a flyout renders in the window's overlay layer, so it can sit
/// perfectly inside its own visual tree while hanging off the pane a person is looking at.
/// </summary>
[Collection("avalonia")]
public class SessionComposerAlignmentTests
{
    private static (Window Window, Button Background, Button Send) Compose()
    {
        var window = new Window { Width = 900, Height = 700, Content = new SessionView { DataContext = new SessionViewModel() } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
        return (window,
            buttons.First(b => b.Name == "BackgroundWorkButton"),
            buttons.First(b => b.Content as string == "Send"));
    }

    [Fact]
    public void TheBackgroundButtonSitsOnTheSameTopAndBottomEdgeAsSend() => HeadlessAvalonia.Run(() =>
    {
        var (window, background, send) = Compose();

        var backgroundTop = background.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        var sendTop = send.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        var backgroundHeight = background.Bounds.Height;
        var sendHeight = send.Bounds.Height;

        window.Close();

        // Equal heights are what makes the shared bottom edge read as alignment. At Padding="8,6" this button
        // measured 30 against Send's 34: same bottom, tops 4px apart.
        Assert.Equal(sendHeight, backgroundHeight);
        Assert.Equal(sendTop, backgroundTop);
    });

    [Fact]
    public void TheBackgroundPopOutOpensIntoThePaneRatherThanOffItsLeftEdge() => HeadlessAvalonia.Run(() =>
    {
        var (window, background, _) = Compose();

        var placement = (background.Flyout as Flyout)?.Placement;
        var buttonWidth = background.Bounds.Width;

        window.Close();

        // Top centres the pop-out on its button. This button is docked left, and the pop-out is far wider than
        // it, so centring pushes the overhang off the left of the session pane and over the sidebar — which is
        // exactly what Raymond saw. Aligning the left edges makes it open rightwards into the pane instead.
        Assert.Equal(PlacementMode.TopEdgeAlignedLeft, placement);
        Assert.True(buttonWidth < 410, "the pop-out is wider than its button, which is why centring overhangs");
    });
}
