using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Files;
using Cockpit.App.Views;
using Cockpit.Core.Layout;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Files;
using Material.Icons;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.App;

internal static partial class Screenshotter
{
    // A strip with a note tool and its pop-out holding the live editor over an in-memory live source, two other people in the file.
    private static Window _CompanionLiveNoteScene(int width, int height)
    {
        var window = new CompanionWindow(null);
        window.SetTools(
        [
            _CompanionSceneItems()[0],
            new("scene.live-note", "Live note", "Live note", string.Empty, MaterialIconKind.NoteTextOutline,
                new SideMenuButtonBadge { Primary = 2 }, () => new TextBlock(), Inline: false),
        ]);
        var canvas = new Canvas { Width = width, Height = height, Tag = window };
        canvas.Bind(Panel.BackgroundProperty, canvas.GetResourceObservable("CockpitWindowBgBrush"));
        canvas.Children.Add(window.TakeCard());

        return new Window { Width = width, Height = height, Content = canvas };
    }

    private static void _StageCompanionLiveNote(Window scene)
    {
        var canvas = (Canvas)scene.Content!;
        var card = (Control)canvas.Children[0];
        var workArea = new PixelRect(0, 0, (int)canvas.Width, (int)canvas.Height);
        var size = _Settled(scene, card);
        var at = new PixelPoint(workArea.Width - size.Width - 24, 24);
        Canvas.SetLeft(card, at.X);
        Canvas.SetTop(card, at.Y);
        card.Measure(new Size(size.Width, size.Height));
        card.Arrange(new Rect(at.X, at.Y, size.Width, size.Height));

        var channel = new FileSceneChannel(PresenceNote, writable: true, live: true, presence: true);
        var editor = new LiveMarkdownEditorView(new FileSourceChannelClient(channel), new FileRef("scene", "idea", "Meetings/2026-10-28 Weekly sync.md"), NullLogger.Instance);
        var body = new StackPanel { Margin = new Thickness(16), Spacing = 10, Children = { new TextBlock { Text = "Live note", FontSize = 13, FontWeight = Avalonia.Media.FontWeight.Medium }, editor } };
        var popOut = new CompanionPopOutCard { Body = body };
        for (var pass = 0; pass < 20; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        var button = card.GetVisualDescendants().OfType<CompanionStripButton>().Single();
        var corner = button.TranslatePoint(default, card) ?? default;
        var item = new PixelRect(at.X + (int)corner.X, at.Y + (int)corner.Y, (int)button.Bounds.Width, (int)button.Bounds.Height);
        var desired = new PixelSize(360, 780);
        if (CompanionPopOutPlacer.Place(new PixelRect(24, 24, workArea.Width - 48, workArea.Height - 48), new PixelRect(at, size), CompanionOrientation.Horizontal, item, desired, []) is not { } placement)
        {
            throw new InvalidOperationException("The live-note pop-out has no room on the stand-in screen.");
        }

        _Put(canvas, _Inflate(placement.Bounds, CompanionPopOutCard.ShadowMargin), popOut);
    }
}
