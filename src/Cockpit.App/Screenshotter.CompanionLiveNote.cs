using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Cockpit.App.Controls;
using Avalonia.Threading;
using Cockpit.App.Files;
using Cockpit.App.Views;
using Cockpit.Plugins.Abstractions.Files;
using Material.Icons;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.App;

internal static partial class Screenshotter
{
    // The live-note pop-out under its strip item, as the mockup's "live" scene. The Depot plugin's own view cannot be
    // built from the host, so this lays out the same pieces it does (DepotLiveNoteView): the notes as tabs, the host's
    // live editor in a 190px well, and the foot — under the head every plugin pop-out gets.
    private static Window _CompanionLiveNoteScene(int width, int height)
    {
        var channel = new FileSceneChannel(PresenceNote, writable: true, live: true, presence: true);
        var editor = new LiveMarkdownEditorView(new FileSourceChannelClient(channel), new FileRef("scene", "idea", "Meetings/2026-10-28 Weekly sync.md"), NullLogger.Instance);
        for (var pass = 0; pass < 20; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        tabs.Children.Add(new Button { Content = "Weekly sync", Classes = { "Segment", "on" } });
        tabs.Children.Add(new Button { Content = "Standup notes", Classes = { "Segment" } });
        var track = new Border { CornerRadius = new CornerRadius(20), Padding = new Thickness(2), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, Child = tabs };
        track.Bind(Border.BackgroundProperty, track.GetResourceObservable("CockpitInsetBgBrush"));
        track.Bind(Border.BorderBrushProperty, track.GetResourceObservable("CockpitHairlineBrush"));
        var well = new Border
        {
            Height = 190, ClipToBounds = true, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Padding = new Thickness(2),
            Child = new ScrollViewer { Content = editor, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
        };
        well.Bind(Border.BackgroundProperty, well.GetResourceObservable("CockpitWindowBgBrush"));
        well.Bind(Border.BorderBrushProperty, well.GetResourceObservable("CockpitHairlineSoftBrush"));
        var saved = CockpitIcons.Icon(MaterialIconKind.CloudCheckOutline, 14);
        saved.Bind(Material.Icons.Avalonia.MaterialIcon.ForegroundProperty, saved.GetResourceObservable("CockpitStatusDoneBrush"));
        var auto = new CheckBox { Content = "Open new notes automatically", Classes = { "Switch" }, FontSize = 11, IsChecked = true };
        var where = CompanionPanelChrome.Text("Live in Depot · cockpit", "CockpitTextSecondaryBrush", 11);
        where.Margin = new Thickness(6, 0, 8, 0);
        where.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(saved, Dock.Left);
        DockPanel.SetDock(auto, Dock.Right);
        var foot = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 8, 0, 0), Child = new DockPanel { Children = { saved, auto, where } } };
        foot.Bind(Border.BorderBrushProperty, foot.GetResourceObservable("CockpitHairlineSoftBrush"));
        var view = new StackPanel { Width = 370, Spacing = 8, Margin = CompanionPanelChrome.BodyMargin, Children = { track, well, foot } };
        var body = new StackPanel { Children = { CompanionPanelChrome.Header("Live note", () => { }), view } };

        var strip = new CompanionWindow(null);
        strip.SetTools(_CompanionSceneItems(livePeople: 2));

        return _CompanionDeskScene(width, height, new CompanionDesk(strip, card => _StripItem(card, "depot.live-note"), body, 400));
    }
}
