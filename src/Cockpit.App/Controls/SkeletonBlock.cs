using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Cockpit.App.Controls;

// AC-1829: a grey placeholder with a soft highlight sweeping across it, standing where something is still loading.
// Stagger starts neighbouring blocks a beat apart so a stack of them reads as a wave, not as one flashing slab.
public sealed class SkeletonBlock : Border
{
    private const double SweepWidth = 120;

    public static readonly StyledProperty<int> StaggerProperty =
        AvaloniaProperty.Register<SkeletonBlock, int>(nameof(Stagger));

    private readonly Border _sweep;
    private readonly TranslateTransform _travel = new();
    private CancellationTokenSource? _running;

    public SkeletonBlock()
    {
        ClipToBounds = true;
        CornerRadius = new CornerRadius(6);
        Height = 12;
        this.Bind(BackgroundProperty, this.GetResourceObservable("CockpitInsetBgBrush"));

        _sweep = new Border
        {
            Width = SweepWidth,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            IsHitTestVisible = false,
            RenderTransform = _travel,
            OpacityMask = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                [
                    new GradientStop(Colors.Transparent, 0),
                    new GradientStop(Colors.Black, 0.5),
                    new GradientStop(Colors.Transparent, 1),
                ],
            },
        };
        _sweep.Bind(BackgroundProperty, this.GetResourceObservable("CockpitHairlineBrush"));
        Child = _sweep;
    }

    public int Stagger
    {
        get => GetValue(StaggerProperty);
        set => SetValue(StaggerProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _running = new CancellationTokenSource();
        _ = _SweepAsync(_running.Token);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _running?.Cancel();
        _running = null;
        base.OnDetachedFromVisualTree(e);
    }

    private async Task _SweepAsync(CancellationToken token)
    {
        // The travel depends on how wide this block turned out, so it is built per run, not once.
        var width = Bounds.Width > 0 ? Bounds.Width : 240;
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(1400),
            Delay = TimeSpan.FromMilliseconds(Stagger * 140),
            IterationCount = IterationCount.Infinite,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(TranslateTransform.XProperty, -SweepWidth) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(TranslateTransform.XProperty, width) } },
            },
        };

        try
        {
            await animation.RunAsync(_sweep, token);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
