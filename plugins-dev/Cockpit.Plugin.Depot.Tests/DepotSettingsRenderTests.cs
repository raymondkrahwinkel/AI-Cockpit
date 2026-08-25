using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Settings;
using Cockpit.Plugin.Depot.Ui;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// The Depot settings view, drawn (AC-243, IL#9): unlike the rest of the suite, which asks whether
// `DepotSettingsControl.Save` behaves, this asks what the view looks like in the cockpit's own theme — a control
// styled outside a shown window renders foreground-on-background as though the theme never loaded.
[Collection("avalonia")]
public class DepotSettingsRenderTests
{
    [Fact]
    public void SavedConnectionsRow_RendersItsNameAndUrl_LegibleAgainstTheTheme()
    {
        var storage = new FakePluginStorage();
        var settings = new DepotSettings(storage)
        {
            Connections = [new DepotConnectionRegistration("conn-1", "Work", "https://depot.example.com")],
        };
        var host = Substitute.For<ICockpitHost>();
        host.CreateHelpHint(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(_ => new Panel());

        var view = new DepotSettingsControl(host, settings);
        var image = _Render(view, 640, 420, "depot-settings.png");

        var nameBox = view.GetVisualDescendants().OfType<TextBox>().First(box => box.Text == "Work");
        var urlBox = view.GetVisualDescendants().OfType<TextBox>().First(box => box.Text == "https://depot.example.com");

        // Pins the exact fault the Workflows harness exists to catch: a TextBox laid out outside a shown window
        // renders its text in near-black on a dark fill. Legible here is also how this test proves it is honest —
        // the theme really loaded rather than every named brush quietly falling back to Fluent.
        Assert.True(_BrightestPixelIn(image, nameBox, view) > 140, "the 'Work' row's name is not legible against the theme");
        Assert.True(_BrightestPixelIn(image, urlBox, view) > 140, "the 'Work' row's URL is not legible against the theme");
    }

    private static int _BrightestPixelIn(WriteableBitmap image, Visual control, Visual root)
    {
        var origin = control.TranslatePoint(default, root)
            ?? throw new InvalidOperationException("The control is not in the tree that was rendered.");

        var left = Math.Max(0, (int)origin.X);
        var top = Math.Max(0, (int)origin.Y);
        var right = Math.Min(image.PixelSize.Width - 1, left + (int)control.Bounds.Width);
        var bottom = Math.Min(image.PixelSize.Height - 1, top + (int)control.Bounds.Height);

        var brightest = 0;
        using var buffer = image.Lock();
        var stride = buffer.RowBytes;
        var pixels = new byte[stride * image.PixelSize.Height];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address, pixels, 0, pixels.Length);

        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var offset = (y * stride) + (x * 4);
                // Bgra8888.
                brightest = Math.Max(brightest, Math.Max(pixels[offset + 2], Math.Max(pixels[offset + 1], pixels[offset])));
            }
        }

        return brightest;
    }

    private static WriteableBitmap _Render(Control control, int width, int height, string fileName)
    {
        var root = new Border
        {
            Width = width,
            Height = height,
            Background = Application.Current?.FindResource("CockpitWindowBgBrush") as IBrush ?? Brushes.Black,
            Child = control,
        };

        var window = new Window { Width = width, Height = height, Content = root };
        window.Show();
        window.UpdateLayout();

        var target = new RenderTargetBitmap(new PixelSize(width, height));
        target.Render(root);
        window.Close();

        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        target.Save(path);

        using var stream = File.OpenRead(path);
        return WriteableBitmap.Decode(stream);
    }
}
