using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Cockpit.App.Services;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.App;

internal static partial class Screenshotter
{
    // The status pop-out open under its counters, as the mockup's "status" scene; "waiting" and "quiet" leave fewer
    // sessions running.
    private static Window _CompanionStatusScene(int width, int height, string state)
    {
        var feed = _SceneFeed();
        var cockpit = feed.Cockpit;
        var sessions = feed.StatusSessions.ToList();
        var panel = new CompanionStatusPanel(feed);
        var waiting = sessions.Single(session => session.SessionStatus == SessionStatus.NeedsAttention);
        var running = cockpit.SelectedSession!;
        var pick = panel.GetLogicalDescendants().OfType<Button>().Single(button => ReferenceEquals(button.Tag, waiting));
        pick.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!ReferenceEquals(cockpit.SelectedSession, waiting))
        {
            throw new InvalidOperationException("The status panel did not select its agent.");
        }

        cockpit.SelectedSession = running;
        if (state != "all")
        {
            foreach (var session in sessions.Where(session => session.SessionStatus == SessionStatus.Busy))
            {
                session.SessionStatus = SessionStatus.Idle;
            }
        }

        if (state == "quiet")
        {
            waiting.SessionStatus = SessionStatus.Idle;
        }

        feed.RefreshStatus(sessions);
        if (state == "all" && (feed.RunningCount != 2 || feed.WaitingCount != 1 || feed.BackgroundCount != 1))
        {
            throw new InvalidOperationException($"Running={feed.RunningCount}, waiting={feed.WaitingCount}, background={feed.BackgroundCount}; expected 2, 1 and 1.");
        }

        var strip = new CompanionWindow(null);
        strip.SetTools(_CompanionSceneItems(feed));

        return _CompanionDeskScene(width, height, new CompanionDesk(strip,
            card => _StripItem(card, CompanionAttentionFeed.StatusToolId), state == "all" ? panel : null, 340));
    }
}
