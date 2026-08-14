using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cockpit.App.Controls;
using Cockpit.Core.Sessions;

namespace Cockpit.App.Views;

// The mini-gallery a clicked "[+N image]" chip opens (AC-778): the images a user message carried, shown one at
// a time with previous/next navigation and a fit-to-window/1:1 toggle. Built from the in-memory
// `ImageAttachment` bytes the row's own `TranscriptEntryViewModel.Images` still holds — there is no on-disk
// transcript to read them back from, so this window only ever exists for a message from the running session.
public partial class ImagePreviewWindow : Window
{
    // Ctrl+scroll zoom range, layered on top of whichever Fit/1:1 baseline is active (AC-778 follow-up).
    private const double MinZoom = 0.2;
    private const double MaxZoom = 6.0;
    private const double ZoomStepBase = 1.15;

    private IReadOnlyList<ImageAttachment> _images = [];
    private int _index;
    private Bitmap? _bitmap;
    private double _zoom = 1.0;

    public ImagePreviewWindow()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this, "Image preview");
        Checkerboard.Background = FilePreviewWindow.CheckerboardBrush();

        // The chrome itself has no outer edge (AC-678 dropped OS decorations everywhere) — against a similarly
        // dark desktop this window otherwise has no visible boundary at all. Wraps the chrome's own root, so the
        // frame runs around the title bar too, not just the body below it.
        if (Content is Control chrome)
        {
            // Detach before re-parenting under the frame, or Avalonia throws on a control briefly owned by two
            // parents (the same reason CockpitWindowChrome.Apply above does it).
            Content = null;
            Content = new Border
            {
                BorderBrush = _Brush("CockpitHairlineBrush"),
                BorderThickness = new Thickness(1),
                Child = chrome,
            };
        }

        // Tunnel + handledEventsToo, same as SessionView's transcript scroller: this must see the wheel before
        // the ScrollViewer's own presenter claims it for scrolling.
        BodyScroller.AddHandler(InputElement.PointerWheelChangedEvent, _OnBodyWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;

    public static void Show(IReadOnlyList<ImageAttachment> images, int startIndex, Window owner) =>
        Build(images, startIndex).Show(owner);

    // The render harness's own step (the same split FilePreviewWindow.Build/ScreenshotPreviewWindow.Build make):
    // the window built and showing its first image, without being put on screen. `_ShowImage` is the one place
    // that guards a bad index (empty list, or one out of range) — an out-of-range `startIndex` here just leaves
    // the window on its unpopulated defaults rather than this method second-guessing it too.
    internal static ImagePreviewWindow Build(IReadOnlyList<ImageAttachment> images, int startIndex)
    {
        var window = new ImagePreviewWindow { _images = images };
        window._ShowImage(startIndex);
        return window;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private void _OnPrevious(object? sender, RoutedEventArgs e) => _ShowImage(_index - 1);

    private void _OnNext(object? sender, RoutedEventArgs e) => _ShowImage(_index + 1);

    private void _OnFit(object? sender, RoutedEventArgs e)
    {
        PreviewImage.Stretch = Stretch.Uniform;
        _ApplyZoom(1.0);
    }

    // 1:1: the image keeps its own pixel size and the surrounding ScrollViewer picks up scrollbars once it no
    // longer fits — no pan/deep-zoom (out of v1 scope), just the plain "actual size" the ticket asks for.
    private void _OnActualSize(object? sender, RoutedEventArgs e)
    {
        PreviewImage.Stretch = Stretch.None;
        _ApplyZoom(1.0);
    }

    // Ctrl+scroll zooms around whichever Fit/1:1 baseline Fit/1:1 last set, the same gesture as browsers and
    // image viewers. Plain scroll is left alone, so the ScrollViewer still pans an image too big for the window.
    private void _OnBodyWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        e.Handled = true;
        _ApplyZoom(_zoom * Math.Pow(ZoomStepBase, e.Delta.Y));
    }

    private void _ApplyZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        var transform = (ScaleTransform)PreviewImage.RenderTransform!;
        transform.ScaleX = _zoom;
        transform.ScaleY = _zoom;
    }

    private void _ShowImage(int index)
    {
        if (_images.Count == 0 || index < 0 || index >= _images.Count)
        {
            return;
        }

        _index = index;
        var image = _images[index];
        _bitmap?.Dispose();
        using (var stream = new MemoryStream(Convert.FromBase64String(image.Base64Data)))
        {
            _bitmap = new Bitmap(stream);
        }

        PreviewImage.Source = _bitmap;
        _ApplyZoom(1.0);
        CountText.Text = _images.Count == 1 ? "Image" : $"Image {index + 1} of {_images.Count}";
        NavigationRow.IsVisible = _images.Count > 1;
        PreviousButton.IsEnabled = index > 0;
        NextButton.IsEnabled = index < _images.Count - 1;
    }
}
