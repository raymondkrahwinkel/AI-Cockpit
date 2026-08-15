using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cockpit.App.Controls;
using Cockpit.Core.Sessions;

namespace Cockpit.App.Views;

// The mini-gallery a clicked "[+N image]" chip opens (AC-778): the images a user message carried, shown one
// at a time with previous/next navigation and a Fit/1:1/pan toggle. Built from the in-memory `ImageAttachment`
// bytes the row's own `TranscriptEntryViewModel.Images` still holds — no on-disk transcript to read them from.
public partial class ImagePreviewWindow : Window
{
    // Ctrl+scroll zoom range, layered on top of whichever Fit/1:1 baseline is active (AC-778 follow-up).
    private const double MinZoom = 0.2;
    private const double MaxZoom = 6.0;
    private const double ZoomStepBase = 1.15;

    private static readonly Cursor PannableCursor = new(StandardCursorType.Hand);
    private static readonly Cursor PanningCursor = new(StandardCursorType.SizeAll);

    private IReadOnlyList<ImageAttachment> _images = [];
    private int _index;
    private Bitmap? _bitmap;
    private double _zoom = 1.0;
    private bool _isPanning;
    private Point _panPointerStart;
    private Vector _panOffsetStart;

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
    // the window built and showing its first image, without being on screen. `_ShowImage` guards a bad index
    // (empty list, out of range) — an out-of-range `startIndex` just leaves the window on its unpopulated defaults.
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

    private void _OnFit(object? sender, RoutedEventArgs e) => _SetStretch(Stretch.Uniform);

    // 1:1: the image keeps its own pixel size; the ScrollViewer picks up scrollbars — and now panning — once
    // it no longer fits.
    private void _OnActualSize(object? sender, RoutedEventArgs e) => _SetStretch(Stretch.None);

    // Fit only fits while the scroller constrains the image: a ScrollViewer that may scroll measures its child
    // unbounded, and Stretch.Uniform then resolves to the image's own pixels — the same box 1:1 gives, which is
    // why both buttons looked dead. So scrolling goes off for Fit and back on for 1:1.
    private void _SetStretch(Stretch stretch)
    {
        PreviewImage.Stretch = stretch;
        PreviewImage.Width = double.NaN;
        PreviewImage.Height = double.NaN;
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

        // AC-804: away from zoom 1, LayoutTransformControl's inverse-scaled constraint makes Stretch.Uniform
        // re-fit and cancel the transform out — freezing the box first stops that in both zoom directions.
        if (PreviewImage.Stretch == Stretch.Uniform)
        {
            if (_zoom != 1.0 && double.IsNaN(PreviewImage.Width))
            {
                PreviewImage.Width = PreviewImage.Bounds.Width;
                PreviewImage.Height = PreviewImage.Bounds.Height;
            }
            else if (_zoom == 1.0)
            {
                PreviewImage.Width = double.NaN;
                PreviewImage.Height = double.NaN;
            }
        }

        var transform = (ScaleTransform)PreviewImageZoom.LayoutTransform!;
        transform.ScaleX = _zoom;
        transform.ScaleY = _zoom;

        var scrollbars = _CanPan() ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        BodyScroller.HorizontalScrollBarVisibility = scrollbars;
        BodyScroller.VerticalScrollBarVisibility = scrollbars;
        if (!_isPanning)
        {
            BodyScroller.Cursor = _CanPan() ? PannableCursor : Cursor.Default;
        }
    }

    private bool _CanPan() => PreviewImage.Stretch == Stretch.None || _zoom > 1.0;

    private void _OnBodyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_CanPan() || !e.GetCurrentPoint(BodyScroller).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isPanning = true;
        _panPointerStart = e.GetPosition(BodyScroller);
        _panOffsetStart = BodyScroller.Offset;
        e.Pointer.Capture(BodyScroller);
        BodyScroller.Cursor = PanningCursor;
    }

    private void _OnBodyPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        // ScrollViewer.Offset coerces to the valid [0, Extent-Viewport] range on its own, so this can't drag the
        // image past its own edges.
        var moved = _panPointerStart - e.GetPosition(BodyScroller);
        BodyScroller.Offset = _panOffsetStart + moved;
    }

    private void _OnBodyPointerReleased(object? sender, PointerReleasedEventArgs e) => _EndPan();

    private void _OnBodyPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _EndPan();

    private void _EndPan()
    {
        if (!_isPanning)
        {
            return;
        }

        _isPanning = false;
        BodyScroller.Cursor = _CanPan() ? PannableCursor : Cursor.Default;
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
        PreviewImage.Width = double.NaN;
        PreviewImage.Height = double.NaN;
        BodyScroller.Offset = default;
        _ApplyZoom(1.0);
        CountText.Text = _images.Count == 1 ? "Image" : $"Image {index + 1} of {_images.Count}";
        NavigationRow.IsVisible = _images.Count > 1;
        PreviousButton.IsEnabled = index > 0;
        NextButton.IsEnabled = index < _images.Count - 1;
    }
}
