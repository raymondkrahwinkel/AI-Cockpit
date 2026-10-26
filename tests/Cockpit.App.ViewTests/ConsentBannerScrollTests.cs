using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Cockpit.App.ViewTests;

// AC-1523: an approval card with a 3,000-character action grew past the window, so Approve sat off screen and the
// operator could only deny. Read off the laid-out window: the button has to be inside it and be what a click there hits.
[Collection("avalonia")]
public class ConsentBannerScrollTests
{
    [Fact]
    public void ALongAction_ScrollsInsideTheCard_AndApproveStaysOnScreenAndClickable()
    {
        var (inside, hit) = HeadlessAvalonia.Run(() =>
        {
            var window = Screenshotter.BuildScene("assistant-chat-consent-long", 0, 0);
            window.Show();
            try
            {
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                Dispatcher.UIThread.RunJobs();

                var approve = window.GetVisualDescendants().OfType<Button>()
                    .Single(button => Equals(button.Content, "Approve") && button.IsEffectivelyVisible);
                var corner = approve.TranslatePoint(new Point(approve.Bounds.Width, approve.Bounds.Height), window);
                var centre = approve.TranslatePoint(new Point(approve.Bounds.Width / 2, approve.Bounds.Height / 2), window);
                var inside = corner is { } c && c.Y <= window.ClientSize.Height && c.X <= window.ClientSize.Width;
                var hit = centre is { } p && window.InputHitTest(p) is Visual target
                    && (target == approve || target.GetVisualAncestors().Contains(approve));
                return (inside, hit);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(inside, "Approve is laid out below or beside the window.");
        Assert.True(hit, "A click on Approve does not reach Approve.");
    }
}
