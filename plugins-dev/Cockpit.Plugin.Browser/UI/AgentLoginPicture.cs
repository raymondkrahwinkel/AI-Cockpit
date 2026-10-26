using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Cockpit.Plugin.Browser.UI;

// The page a scene's agent tab shows (mockup screen 5): a login form drawn once into a bitmap, standing in for the
// screencast frame, since no Chromium runs in a headless render.
internal static class AgentLoginPicture
{
    public static Bitmap Render(int width, int height)
    {
        var page = new Border
        {
            Width = width,
            Height = height,
            Background = Brushes.White,
            Child = new Canvas { Children = { Text("Wispslate Depot", 22, 28, 15, bold: true), Text("Docs", 152, 28, 13), Text("Pricing", 194, 28, 13), Text("Sign in", width - 80, 28, 13), Rule(70, width), Text("Sign in", 22, 110, 20, bold: true), Field(22, 150, "test-agent@example.test"), Field(22, 194, "••••••••", focused: true), Button(22, 240) } },
        };
        page.Measure(new Size(width, height));
        page.Arrange(new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(page);
        return bitmap;
    }

    private static TextBlock Text(string text, double x, double y, double size, bool bold = false)
    {
        var block = new TextBlock { Text = text, FontSize = size, Foreground = new SolidColorBrush(Color.Parse("#1B1F27")), FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal };
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        return block;
    }

    private static Rectangle Rule(double y, double width)
    {
        var rule = new Rectangle { Width = width, Height = 1, Fill = new SolidColorBrush(Color.Parse("#DDE1E7")) };
        Canvas.SetTop(rule, y);
        return rule;
    }

    private static Border Field(double x, double y, string value, bool focused = false)
    {
        var field = new Border
        {
            Width = 360,
            Height = 34,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(focused ? 2 : 1),
            BorderBrush = new SolidColorBrush(Color.Parse(focused ? "#A67CD8" : "#C5CAD3")),
            Padding = new Thickness(10, 0),
            Child = new TextBlock { Text = value, FontSize = 13, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.Parse("#1B1F27")) },
        };
        Canvas.SetLeft(field, x);
        Canvas.SetTop(field, y);
        return field;
    }

    private static Border Button(double x, double y)
    {
        var button = new Border
        {
            Width = 360,
            Height = 34,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.Parse("#2F6FE4")),
            Child = new TextBlock { Text = "Sign in", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
        };
        Canvas.SetLeft(button, x);
        Canvas.SetTop(button, y);
        return button;
    }
}
