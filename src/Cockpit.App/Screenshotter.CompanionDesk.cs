using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Layout;
using Cockpit.Plugins.Abstractions;
using Material.Icons;

namespace Cockpit.App;

internal static partial class Screenshotter
{
    // AC-2190: the companion-strook mockup's edge — the strip and every pop-out keep 8px off the screen's edges.
    private const int DeskEdge = 8;

    // One companion scene as the mockup lays it out: the real strip at an edge of a stand-in screen (top right unless
    // the scene says otherwise), and the pop-out of one of its items where CompanionPopOutPlacer puts it. Anything else
    // the scene needs on the screen (the look-along frame, an open flyout) comes after, in Extra.
    private sealed record CompanionDesk(
        CompanionWindow Strip,
        Func<Control, Control>? Anchor = null,
        Control? PopOut = null,
        double PopOutWidth = 0,
        string Position = "tr",
        Action<Canvas, PixelRect>? Extra = null);

    private static CompanionOrientation _SceneOrientation => CompanionSceneOrientation == "v" ? CompanionOrientation.Vertical : CompanionOrientation.Horizontal;

    private static Window _CompanionDeskScene(int width, int height, CompanionDesk desk)
    {
        desk.Strip.ApplyLayout(_SceneOrientation, alwaysOnTop: true);
        var canvas = new Canvas { Width = width, Height = height, Tag = desk };
        canvas.Bind(Panel.BackgroundProperty, canvas.GetResourceObservable("CockpitWindowBgBrush"));
        canvas.Children.Add(desk.Strip.TakeCard());

        return new Window { Width = width, Height = height, Content = canvas };
    }

    private static void _StageCompanionDesk(Window scene)
    {
        var canvas = (Canvas)scene.Content!;
        var desk = (CompanionDesk)canvas.Tag!;
        var card = (Control)canvas.Children[0];
        var position = desk.Position;
        if (position.Length != 2 || "tmb".IndexOf(position[0]) < 0 || "lcr".IndexOf(position[1]) < 0)
        {
            throw new ArgumentException($"--pos must be one of tl tc tr ml mc mr bl bc br; it is \"{position}\".");
        }

        var width = (int)canvas.Width;
        var height = (int)canvas.Height;
        var margin = CompanionPopOutCard.ShadowMargin;
        var workArea = new PixelRect(DeskEdge, DeskEdge, width - 2 * DeskEdge, height - 2 * DeskEdge);
        var size = _Settled(scene, card);
        var visible = new PixelSize(size.Width - 2 * margin, size.Height - 2 * margin);
        var strip = new PixelRect(
            position[1] switch { 'l' => workArea.X, 'r' => workArea.Right - visible.Width, _ => (width - visible.Width) / 2 },
            position[0] switch { 't' => workArea.Y, 'b' => workArea.Bottom - visible.Height, _ => (height - visible.Height) / 2 },
            visible.Width, visible.Height);
        if (!workArea.Contains(strip))
        {
            throw new InvalidOperationException($"The strip at {strip} runs outside the work area {workArea}.");
        }

        Canvas.SetLeft(card, strip.X - margin);
        Canvas.SetTop(card, strip.Y - margin);
        card.Measure(new Size(size.Width, size.Height));
        card.Arrange(new Rect(strip.X - margin, strip.Y - margin, size.Width, size.Height));
        scene.UpdateLayout();

        if (desk.PopOut is { } body && desk.Anchor is { } anchor)
        {
            var item = anchor(card);
            item.Classes.Set("open", true);
            var corner = item.TranslatePoint(default, canvas) ?? throw new InvalidOperationException("The pop-out's item is not on the strip.");
            var itemRect = new PixelRect((int)corner.X, (int)corner.Y, (int)item.Bounds.Width, (int)item.Bounds.Height);
            // On the screen first, as CompanionPopOut has it in its window: a view is only styled, and so only has a
            // height, once it is in a tree.
            var popOut = new CompanionPopOutCard { Body = body, Width = desk.PopOutWidth + 2 * margin };
            canvas.Children.Add(popOut);
            scene.UpdateLayout();
            body.Measure(new Size(desk.PopOutWidth - 2, double.PositiveInfinity));
            var desired = new PixelSize((int)desk.PopOutWidth, (int)Math.Ceiling(body.DesiredSize.Height) + 2);
            if (CompanionPopOutPlacer.Place(workArea, strip, _SceneOrientation, itemRect, desired, []) is not { } placement)
            {
                throw new InvalidOperationException("The pop-out has no room on the stand-in screen.");
            }

            canvas.Children.Remove(popOut);
            _Put(canvas, _Inflate(placement.Bounds, margin), popOut);
        }

        desk.Extra?.Invoke(canvas, strip);
        scene.UpdateLayout();
    }

    // The strip of the mockup, in its order: the assistant, the screenshot split, status, notifications, then the three
    // plugin items it shows (live notes, local checks, pull requests).
    private static List<CompanionStripItem> _CompanionSceneItems(
        CompanionAttentionFeed? feed = null, int questions = 0, int needYou = 0, CockpitViewModel? cockpit = null,
        Func<Control>? notifications = null, Func<Control>? liveNote = null, int livePeople = 0)
    {
        feed ??= _SceneFeed();
        cockpit ??= feed.Cockpit;
        var bell = needYou > 0 ? new SideMenuButtonBadge { Primary = needYou } : null;
        var notes = livePeople > 0 ? new SideMenuButtonBadge { Text = livePeople.ToString(System.Globalization.CultureInfo.InvariantCulture), Tone = SideMenuButtonBadgeTone.Success } : null;

        return
        [
            new("cockpit.assistant", "Assistant", string.Empty, string.Empty, MaterialIconKind.Microphone, null, () => new AssistantIndicator
            {
                ShowWhenDisabled = true,
                DataContext = new AssistantIndicatorViewModel
                {
                    IsFeatureEnabled = true,
                    Activity = Core.Assistant.AssistantActivity.Ready,
                    OpenQuestionCount = questions,
                },
            }, Inline: true),
            new("cockpit.screenshot", "Screenshot", "Take a screenshot", string.Empty, MaterialIconKind.MonitorScreenshot, null,
                () => new CompanionScreenshotView(new CompanionSceneStorage(), cockpit, () => Task.CompletedTask, () => Task.CompletedTask), Inline: true),
            new(CompanionAttentionFeed.StatusToolId, "Status", "Cockpit status", string.Empty, MaterialIconKind.Pulse, null,
                () => new CompanionStatusPanel(feed), Inline: false, () => new CompanionStatusCounters(feed), OwnHead: true),
            new(CompanionAttentionFeed.ToolId, "Notifications", "Notifications", string.Empty, MaterialIconKind.BellOutline, bell,
                notifications ?? (() => new CompanionNotificationsPanel([], _ => { }, _ => { })), Inline: false, OwnHead: true),
            new("depot.live-note", "Live note", "Live notes · Depot", string.Empty, MaterialIconKind.NoteEditOutline, notes,
                liveNote ?? (() => new TextBlock()), Inline: false),
            new("local-ci.checks", "Local checks", "Local checks · Local CI", string.Empty, MaterialIconKind.CheckCircleOutline, null,
                () => _SceneBody(1), Inline: false),
            new("github-pull-requests.queue", "Pull requests", "Pull requests · GitHub pull requests", string.Empty, MaterialIconKind.SourcePull,
                new SideMenuButtonBadge { Text = "2", Tone = SideMenuButtonBadgeTone.Accent }, () => _SceneBody(1), Inline: false),
        ];
    }

    // The mockup's six sessions: two running, one waiting, one in the background, one done, one terminal.
    private static CompanionAttentionFeed _SceneFeed()
    {
        var cockpit = new CockpitViewModel();
        cockpit.Sessions.Clear();
        cockpit.DelegatedTasks.Tasks.Clear();
        cockpit.Projects.Projects.Add(new Core.Projects.Project("cockpit", "Cockpit"));
        var running = new SessionViewModel { Title = "default · AC-2141", SessionStatus = SessionStatus.Busy, Statusline = "Running tests" };
        var thinking = new SessionViewModel { Title = "AC-2187 docs sweep", SessionStatus = SessionStatus.Busy, Statusline = "Thinking…" };
        var waiting = new SessionViewModel { Title = "AC-2100 note images", SessionStatus = SessionStatus.NeedsAttention, Statusline = "Waiting for you" };
        var background = new SessionViewModel { Title = "Autopilot · nightly", SessionStatus = SessionStatus.Busy, Statusline = "Working in the background" };
        var done = new SessionViewModel { Title = "Review #1912", SessionStatus = SessionStatus.Done, Statusline = "Done 4 min ago" };
        var terminal = new TtyViewModel { Title = "terminal · infra", SessionStatus = SessionStatus.Idle };
        SessionPanelViewModel[] all = [running, thinking, waiting, background, done, terminal];
        foreach (var session in all)
        {
            session.ProjectId = "cockpit";
            if (!ReferenceEquals(session, background))
            {
                cockpit.Sessions.Add(session);
            }
        }

        cockpit.SelectedSession = running;
        var feed = new CompanionAttentionFeed(cockpit, new CompanionToolRegistry());
        feed.RefreshStatus([.. all]);

        return feed;
    }

    private static Control _StripItem(Control card, string id) => id switch
    {
        "cockpit.assistant" => card.GetVisualDescendants().OfType<AssistantIndicator>().Single(),
        "cockpit.screenshot" => card.GetVisualDescendants().OfType<CompanionScreenshotView>().Single(),
        "more" => card.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "MoreButton"),
        _ => card.GetVisualDescendants().OfType<CompanionStripButton>().Single(button => button.Tag as string == id),
    };
}
