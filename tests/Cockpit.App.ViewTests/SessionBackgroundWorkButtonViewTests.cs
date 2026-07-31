using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// Render-level check for the one doc-comment claim in the composer's XAML that a viewmodel-only test cannot
/// pin down: the background-work button is "always shown, like Stop/Screenshot" (AC-531) — unlike its own count
/// badge, it carries no <c>IsVisible</c> binding at all, so this actually renders <see cref="SessionView"/> with
/// zero outstanding background tasks and asserts the button is there rather than trusting the absence of a
/// binding to mean what the comment says it means.
/// </summary>
[Collection("avalonia")]
public class SessionBackgroundWorkButtonViewTests
{
    private static BackgroundTasksChanged Outstanding(params BackgroundTask[] tasks) =>
        new() { SessionId = "s1", Tasks = tasks };

    [Fact]
    public void WithNoBackgroundWork_TheButtonStillRenders_ButItsBadgeDoesNot() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();

        var window = new Window { Width = 800, Height = 600, Content = new SessionView { DataContext = session } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var button = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "BackgroundWorkButton");
        var badge = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "BackgroundWorkBadge");

        window.Close();

        Assert.True(button.IsEffectivelyVisible, "the background-work button must show even with nothing running (AC-531 #2)");
        Assert.False(badge.IsEffectivelyVisible, "the badge must not show a \"0\" at zero outstanding tasks (AC-531 #2)");
    });

    [Fact]
    public void WithBackgroundWorkOutstanding_TheButtonAndItsBadgeBothRender() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.Apply(Outstanding(new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1")));

        var window = new Window { Width = 800, Height = 600, Content = new SessionView { DataContext = session } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var button = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "BackgroundWorkButton");
        var badge = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "BackgroundWorkBadge");

        window.Close();

        Assert.True(button.IsEffectivelyVisible);
        Assert.True(badge.IsEffectivelyVisible, "one outstanding task must show a badge");
    });
}
