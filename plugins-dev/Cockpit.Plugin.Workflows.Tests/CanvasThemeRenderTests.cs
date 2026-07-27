using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Cockpit.Plugin.Workflows.Canvas;
using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// The canvas, drawn in the cockpit's own colours (AC-337). The rest of this suite asks whether a card behaves;
/// these ask what it looks like — the question the repaint was about, and the one no behavioural test in this repo
/// has ever answered. Twice in this epic a render found a fault a screen full of green tests had walked past.
/// <para>
/// Each test also writes its image next to the test output so it can be opened and looked at. What is asserted is
/// only the part a machine can settle: that the theme is really loaded, and that no kind of step reads as a duller
/// copy of the one that carries the accent.
/// </para>
/// </summary>
[Collection("avalonia")]
public class CanvasThemeRenderTests
{
    /// <summary>How far a stripe's hue has to sit from the accent's, in degrees, to read as another colour.</summary>
    private const double MinimumHueSeparation = 40;

    /// <summary>
    /// Below this, a colour has so little of its hue left that it reads as grey — which is what lets the plain
    /// step's slate sit at the accent's own hue without being mistaken for it.
    /// </summary>
    private const double GreyEnough = 0.25;

    [Fact]
    public void NeitherOtherKindOfStep_ReadsAsAFadedAccent()
    {
        var trigger = _StripeColourOf("cockpit.manual", "Run manually", "canvas-kind-trigger.png");
        var decision = _StripeColourOf("cockpit.if", "Did it pass?", "canvas-kind-decision.png");
        var action = _StripeColourOf("cockpit.notify", "Post the result", "canvas-kind-action.png");

        // What went wrong was not that the old steel blue was *near* the accent — it is 86 apart in plain RGB, which
        // any distance threshold loose enough to be meaningful would wave through. It was that it was the same
        // colour, weaker: a blue at the accent's hue, half its saturation. So that is what this asks. A stripe may
        // be another hue (the decision's gold) or it may have no hue worth speaking of (the plain step's slate),
        // but it may not be a washed-out version of the one colour that means something.
        _AssertNotAFadedCopyOf(trigger, decision, nameof(decision));
        _AssertNotAFadedCopyOf(trigger, action, nameof(action));

        // And the two of them still have to be told apart from each other.
        _AssertNotAFadedCopyOf(decision, action, nameof(action));
    }

    [Fact]
    public void TheTriggersStripe_IsTheThemesAccent_NotAColourOfItsOwn()
    {
        var accent = Application.Current?.FindResource("CockpitAccentColor");

        // If the theme had failed to load, this lookup would miss and the card would fall back to Fluent — the
        // failure mode the fixture exists to rule out, and one that would make every other assertion here a lie.
        Assert.Equal(Color.Parse("#3b82f6"), Assert.IsType<Color>(accent));

        var stripe = _StripeColourOf("cockpit.manual", "Run manually", "canvas-kind-trigger.png");

        Assert.True(_HueSeparation(stripe, (Color)accent) < 5, $"the trigger's stripe is {stripe}, not the accent {accent}");
        Assert.True(Math.Abs(_Saturation(stripe) - _Saturation((Color)accent)) < 0.1, $"the trigger's stripe {stripe} is a weaker accent, not the accent");
    }

    /// <summary>
    /// A card three times its own size, so the thing a 60px-tall card is too small to judge — how its title, its
    /// subtitle and its gear sit together — can actually be looked at. Nothing is asserted: this one is for eyes.
    /// </summary>
    [Fact]
    public void ACard_IsWrittenOutLargeEnoughToRead()
    {
        var card = new WorkflowNodeControl(_Node("cockpit.notify", "Post the result"));
        var zoomed = new Border
        {
            Child = card,
            RenderTransform = new ScaleTransform(3, 3),
            RenderTransformOrigin = RelativePoint.TopLeft,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(Margin),
        };

        var image = _Render(zoomed, 700, 260, "canvas-card-3x.png");

        Assert.Equal(700, image.PixelSize.Width);
    }

    private const int Margin = 20;

    private static void _AssertNotAFadedCopyOf(Color reference, Color candidate, string name)
    {
        var separation = _HueSeparation(reference, candidate);
        var saturation = _Saturation(candidate);

        Assert.True(
            separation > MinimumHueSeparation || saturation < GreyEnough,
            $"{name} {candidate} sits {separation:0}° from {reference} at saturation {saturation:0.00} — same hue, less of it, which reads as the accent gone dull");
    }

    /// <summary>The shorter way round the colour wheel between two colours, in degrees.</summary>
    private static double _HueSeparation(Color left, Color right)
    {
        var difference = Math.Abs(_Hue(left) - _Hue(right));
        return Math.Min(difference, 360 - difference);
    }

    private static double _Hue(Color colour) => new HsvColor(colour).H;

    private static double _Saturation(Color colour) => new HsvColor(colour).S;

    private static WorkflowNode _Node(string typeId, string name) => new()
    {
        Id = name,
        TypeId = typeId,
        Name = name,
    };

    /// <summary>
    /// Renders one card and reads the colour of its leading stripe. The stripe's position is asked of the laid-out
    /// visual tree rather than worked out on paper: a card that has an input pin starts further right than one that
    /// does not, and guessing that offset is how a sampler ends up reporting the colour of a pin.
    /// </summary>
    private static Color _StripeColourOf(string typeId, string name, string fileName)
    {
        var card = new WorkflowNodeControl(_Node(typeId, name));
        var root = new Border
        {
            Background = DotGrid.Build(),
            Child = new StackPanel { Margin = new Thickness(Margin), Children = { card } },
        };

        var image = _Render(root, 560, 120, fileName);

        var stripe = card.GetVisualDescendants().OfType<Border>().First(border => Math.Abs(border.Bounds.Width - 4) < 0.5);
        var centre = stripe.TranslatePoint(new Point(stripe.Bounds.Width / 2, stripe.Bounds.Height / 2), root)
            ?? throw new InvalidOperationException("The stripe is not in the tree that was rendered.");

        return _PixelAt(image, (int)centre.X, (int)centre.Y);
    }

    private static Color _PixelAt(WriteableBitmap image, int x, int y)
    {
        using var buffer = image.Lock();
        var stride = buffer.RowBytes;
        var pixels = new byte[stride];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address + (y * stride), pixels, 0, stride);

        // Bgra8888, the format a decoded PNG lands in here.
        var offset = x * 4;
        return Color.FromArgb(pixels[offset + 3], pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }


    /// <summary>
    /// Renders a control the way the app would: inside a window that has been shown. Measuring and arranging a
    /// loose control is not enough and is quietly misleading — Avalonia applies application styles when a control
    /// reaches a styling root, so a tree with no window above it renders every <c>Foreground</c> and every class
    /// the theme sets as if the theme did not exist. The first version of this harness did exactly that, and the
    /// picture it produced showed a card whose title was drawn in black on a dark card.
    /// </summary>
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

        // Back through a decoded bitmap so individual pixels can be read: RenderTargetBitmap does not expose them.
        using var stream = File.OpenRead(path);
        return WriteableBitmap.Decode(stream);
    }
}
