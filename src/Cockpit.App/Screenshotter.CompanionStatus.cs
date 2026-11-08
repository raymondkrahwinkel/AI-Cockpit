using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Interactivity;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Layout;
using Material.Icons;

namespace Cockpit.App;

internal static partial class Screenshotter
{
    private static Window _CompanionStatusScene(int width, int height, string state)
    {
        var cockpit = new CockpitViewModel();
        cockpit.Sessions.Clear();
        cockpit.DelegatedTasks.Tasks.Clear();
        cockpit.Projects.Projects.Add(new Core.Projects.Project("cockpit", "Cockpit"));
        var running = new SessionViewModel { Title = "Build companion status", SessionStatus = SessionStatus.Busy };
        var waiting = new SessionViewModel { Title = "Review parser", SessionStatus = SessionStatus.NeedsAttention };
        var background = new SessionViewModel { Title = "Autopilot / release notes", SessionStatus = SessionStatus.Busy };
        var terminal = new TtyViewModel { Title = "Terminal / workspace", SessionStatus = SessionStatus.Idle };
        cockpit.Sessions.Add(running);
        cockpit.Sessions.Add(waiting);
        cockpit.Sessions.Add(terminal);
        cockpit.SelectedSession = running;
        foreach (var session in new SessionPanelViewModel[] { running, waiting, background, terminal })
        {
            session.ProjectId = "cockpit";
        }
        var feed = new CompanionAttentionFeed(cockpit, new CompanionToolRegistry());
        feed.RefreshStatus([running, waiting]);
        if (feed.RunningCount != 1)
        {
            throw new InvalidOperationException($"One running and one waiting session counted as {feed.RunningCount} running.");
        }
        var panel = new CompanionStatusPanel(feed);
        var pick = panel.GetLogicalDescendants().OfType<Button>().Last(button => button.Tag is SessionPanelViewModel);
        pick.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!ReferenceEquals(cockpit.SelectedSession, waiting))
        {
            throw new InvalidOperationException("The status panel did not select its agent.");
        }
        cockpit.SelectedSession = running;
        if (state != "all")
        {
            running.SessionStatus = SessionStatus.Idle;
            background.SessionStatus = SessionStatus.Idle;
        }
        if (state == "quiet")
        {
            waiting.SessionStatus = SessionStatus.Idle;
        }
        feed.RefreshStatus([running, waiting, background, terminal]);
        if (state == "all" && (feed.RunningCount != 2 || feed.BackgroundCount != 1))
        {
            throw new InvalidOperationException($"Running={feed.RunningCount}, background={feed.BackgroundCount}; expected 2 and 1.");
        }
        var strip = new CompanionWindow(null);
        var items = _CompanionSceneItems();
        items.Add(
            new CompanionStripItem("cockpit.status", "Status", "Cockpit status", "", MaterialIconKind.CircleOutline, null,
                () => new CompanionStatusPanel(feed), false, () => new CompanionStatusCounters(feed)));
        strip.SetTools(items);
        strip.ApplyLayout(CompanionSceneOrientation == "v" ? CompanionOrientation.Vertical : CompanionOrientation.Horizontal, true);
        var layout = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Spacing = 20, Margin = new Thickness(30) };
        var stripCard = strip.TakeCard();
        stripCard.VerticalAlignment = VerticalAlignment.Top;
        layout.Children.Add(stripCard);
        if (state == "all")
        {
            layout.Children.Add(new CompanionPopOutCard { VerticalAlignment = VerticalAlignment.Top, Width = 360, Body = new CompanionStatusPanel(feed) });
        }
        return new Window { Width = width, Height = height, Content = layout };
    }
}
