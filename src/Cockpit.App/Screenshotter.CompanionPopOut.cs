using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Cockpit.App.Views;

namespace Cockpit.App;

internal static partial class Screenshotter
{
    // "--pos tl|tc|tr|ml|mc|mr|bl|bc|br", "--orient h|v" and "--popouts 1|2" shape the companion-popout scene.
    internal static string CompanionScenePosition { get; set; } = "mc";

    internal static string CompanionSceneOrientation { get; set; } = "h";

    internal static int CompanionScenePopOuts { get; set; } = 2;

    private const int SceneEdge = 16;

    // The screen is the scene window; a stand-in strip and the pop-outs the placer gives it, checked before drawing.
    private static Window _CompanionPopOutScene(int width, int height)
    {
        var horizontal = CompanionSceneOrientation != "v";
        var stripSize = horizontal ? new PixelSize(340, 52) : new PixelSize(64, 260);
        var position = CompanionScenePosition;
        if (position.Length != 2 || "tmb".IndexOf(position[0]) < 0 || "lcr".IndexOf(position[1]) < 0)
        {
            throw new ArgumentException($"--pos must be one of tl tc tr ml mc mr bl bc br; it is \"{position}\".");
        }

        var workArea = new PixelRect(0, 0, width, height);
        var strip = new PixelRect(
            position[1] switch { 'l' => SceneEdge, 'r' => width - stripSize.Width - SceneEdge, _ => (width - stripSize.Width) / 2 },
            position[0] switch { 't' => SceneEdge, 'b' => height - stripSize.Height - SceneEdge, _ => (height - stripSize.Height) / 2 },
            stripSize.Width, stripSize.Height);

        var canvas = new Canvas { Width = width, Height = height };
        canvas.Bind(Panel.BackgroundProperty, canvas.GetResourceObservable("CockpitWindowBgBrush"));
        _Put(canvas, strip, _SceneBox("Strip", fill: "CockpitFloatingCardBrush", corner: true));

        var orientation = horizontal ? CompanionOrientation.Horizontal : CompanionOrientation.Vertical;
        var placed = new List<PixelRect>();
        for (var index = 0; index < CompanionScenePopOuts; index++)
        {
            var item = horizontal
                ? new PixelRect(strip.X + 56 + index * 48, strip.Y + 6, 40, 40)
                : new PixelRect(strip.X + 12, strip.Y + 56 + index * 48, 40, 40);
            _Put(canvas, item, _SceneBox($"{index + 1}", fill: "CockpitAccentBrush"));

            var card = new CompanionPopOutCard { Body = _SceneBody(index + 1) };
            card.Body!.Measure(new Size(360, double.PositiveInfinity));
            var desired = new PixelSize(360, (int)card.Body.DesiredSize.Height);
            if (CompanionPopOutPlacer.Place(workArea, strip, orientation, item, desired, placed) is not { } placement)
            {
                _Put(canvas, new PixelRect(width / 2 - 100, height - 56 - index * 40, 200, 32), _SceneBox($"No room for pop-out {index + 1}", fill: "CockpitPanelBgBrush"));
                continue;
            }

            placed.Add(placement.Bounds);
            _Put(canvas, _Inflate(placement.Bounds, CompanionPopOutCard.ShadowMargin), card);
        }

        _AssertCompanionPlacement(workArea, strip, placed);

        return new Window { Width = width, Height = height, Content = canvas };
    }

    // A pop-out that does not activate leaves the focus on the window that opened it; a render cannot show that, this can fail it.
    private static void _OpenPassivePopOut(Window scene)
    {
        scene.Activate();
        var popOut = new CompanionPopOut { PopOutKey = "passive", Body = new TextBlock { Text = "passive" }, ShowActivated = false };
        popOut.Show(scene);
        Dispatcher.UIThread.RunJobs();
        var stolen = popOut.IsActive;
        popOut.Close();
        if (stolen || !scene.IsActive)
        {
            throw new InvalidOperationException("A pop-out with ShowActivated=false took the focus from the window that opened it.");
        }
    }

    private static PixelRect _Inflate(PixelRect rect, int by) => new(rect.X - by, rect.Y - by, rect.Width + 2 * by, rect.Height + 2 * by);

    private static void _AssertCompanionPlacement(PixelRect workArea, PixelRect strip, List<PixelRect> placed)
    {
        for (var i = 0; i < placed.Count; i++)
        {
            if (!workArea.Contains(placed[i]) || placed[i].Intersects(strip) || placed.Skip(i + 1).Any(placed[i].Intersects))
            {
                throw new InvalidOperationException($"Pop-out {i + 1} at {placed[i]} leaves the work area or lies over the strip or another pop-out.");
            }
        }
    }

    private static void _Put(Canvas canvas, PixelRect rect, Control control)
    {
        control.Width = rect.Width;
        control.Height = rect.Height;
        Canvas.SetLeft(control, rect.X);
        Canvas.SetTop(control, rect.Y);
        canvas.Children.Add(control);
    }

    private static Border _SceneBox(string text, string fill, bool corner = false)
    {
        var label = new TextBlock { Text = text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("CockpitTextPrimaryBrush"));
        if (corner)
        {
            label.HorizontalAlignment = HorizontalAlignment.Left;
            label.VerticalAlignment = VerticalAlignment.Top;
            label.Margin = new Thickness(8, 4);
        }

        var box = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = label };
        box.Bind(Border.BackgroundProperty, box.GetResourceObservable(fill));
        box.Bind(Border.BorderBrushProperty, box.GetResourceObservable("CockpitHairlineBrush"));

        return box;
    }

    // Tall on purpose: a list of rows is what has to scroll when the screen is short.
    private static Control _SceneBody(int number)
    {
        var rows = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        for (var row = 1; row <= 16; row++)
        {
            var text = new TextBlock { Text = $"Pop-out {number}, row {row}", FontSize = 13 };
            text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("CockpitTextPrimaryBrush"));
            rows.Children.Add(text);
        }

        return rows;
    }
}
