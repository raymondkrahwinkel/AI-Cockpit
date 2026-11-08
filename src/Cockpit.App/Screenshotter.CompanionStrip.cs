using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Layout;
using Cockpit.Plugins.Abstractions;
using Material.Icons;

namespace Cockpit.App;

internal static partial class Screenshotter
{
    // The strip of the mockup: the assistant, the screenshot button and the Example tool with a badge.
    private static List<CompanionStripItem> _CompanionSceneItems() =>
    [
        new("cockpit.assistant", "Assistant", string.Empty, string.Empty, MaterialIconKind.Creation, null, () => new AssistantIndicator
        {
            ShowWhenDisabled = true,
            DataContext = new AssistantIndicatorViewModel
            {
                IsFeatureEnabled = true,
                Activity = Core.Assistant.AssistantActivity.Listening,
                ListeningMode = Core.Assistant.AssistantListeningMode.Off,
            },
        }, Inline: true),
        new("cockpit.screenshot", "Screenshot", "Take a screenshot", string.Empty, MaterialIconKind.Monitor, null,
            () => new CompanionScreenshotView(new CompanionSceneStorage(), new CockpitViewModel(),
                () => Task.CompletedTask, () => Task.CompletedTask), Inline: true),
        new("example-companion-tool.hello", "Example", "Example companion tool", string.Empty, MaterialIconKind.HandWave,
            new SideMenuButtonBadge { Primary = 2 }, () => _SceneBody(1), Inline: false),
    ];

    // The real strip on a stand-in screen, laid out the way a switch of orientation does: the grip stays where it
    // was and a strip that would run off the screen is pulled back. "--pos" picks the edge, "--orient" the form.
    // Styles reach the strip only once the scene is shown, so the layout waits for _StageCompanionStrip.
    private static Window _CompanionStripScene(int width, int height)
    {
        var position = CompanionScenePosition;
        if (position.Length != 2 || "tmb".IndexOf(position[0]) < 0 || "lcr".IndexOf(position[1]) < 0)
        {
            throw new ArgumentException($"--pos must be one of tl tc tr ml mc mr bl bc br; it is \"{position}\".");
        }

        var window = new CompanionWindow(null);
        window.SetTools(_CompanionSceneItems());
        var canvas = new Canvas { Width = width, Height = height, Tag = window };
        canvas.Bind(Panel.BackgroundProperty, canvas.GetResourceObservable("CockpitWindowBgBrush"));
        canvas.Children.Add(window.TakeCard());

        return new Window { Width = width, Height = height, Content = canvas };
    }

    private static void _StageCompanionStrip(Window scene)
    {
        var canvas = (Canvas)scene.Content!;
        var window = (CompanionWindow)canvas.Tag!;
        var card = (Control)canvas.Children[0];
        var width = (int)canvas.Width;
        var height = (int)canvas.Height;
        var position = CompanionScenePosition;
        var vertical = CompanionSceneOrientation == "v";
        var workArea = new PixelRect(0, 0, width, height);

        // Where the horizontal strip lies against that edge, then the form asked for on the same grip.
        window.ApplyLayout(CompanionOrientation.Horizontal, alwaysOnTop: true);
        var flat = _Settled(scene, card);
        var origin = new PixelPoint(
            position[1] switch { 'l' => 0, 'r' => width - flat.Width, _ => (width - flat.Width) / 2 },
            position[0] switch { 't' => 0, 'b' => height - flat.Height, _ => (height - flat.Height) / 2 });
        var orientation = vertical ? CompanionOrientation.Vertical : CompanionOrientation.Horizontal;
        window.ApplyLayout(orientation, alwaysOnTop: true);
        var size = _Settled(scene, card);
        var at = CompanionWindow.ClampInto(workArea, new PixelRect(origin, size), CompanionPopOutCard.ShadowMargin);
        Canvas.SetLeft(card, at.X);
        Canvas.SetTop(card, at.Y);
        card.Measure(new Size(size.Width, size.Height));
        card.Arrange(new Rect(at.X, at.Y, size.Width, size.Height));

        var margin = CompanionPopOutCard.ShadowMargin;
        var cardRect = new PixelRect(at.X + margin, at.Y + margin, size.Width - 2 * margin, size.Height - 2 * margin);
        if (!workArea.Contains(cardRect))
        {
            throw new InvalidOperationException($"The strip at {cardRect} runs outside the work area {workArea}.");
        }

        // The Example button opens its pop-out where the placer puts it.
        var example = card.GetVisualDescendants().OfType<CompanionStripButton>().Single();
        var corner = example.TranslatePoint(default, card) ?? default;
        var item = new PixelRect(at.X + (int)corner.X, at.Y + (int)corner.Y, (int)example.Bounds.Width, (int)example.Bounds.Height);
        var popOut = new CompanionPopOutCard { Body = _SceneBody(1) };
        popOut.Body!.Measure(new Size(360, double.PositiveInfinity));
        var desired = new PixelSize(360, (int)popOut.Body.DesiredSize.Height);
        if (CompanionPopOutPlacer.Place(workArea, new PixelRect(at, size), orientation, item, desired, []) is { } placement)
        {
            _Put(canvas, _Inflate(placement.Bounds, margin), popOut);
        }
    }

    // The size a layout pass settles on, which a bare Measure of a control that was just changed does not give.
    private static PixelSize _Settled(Window scene, Control control)
    {
        scene.UpdateLayout();

        return new PixelSize((int)Math.Ceiling(control.DesiredSize.Width), (int)Math.Ceiling(control.DesiredSize.Height));
    }
}
