using Avalonia.Controls;
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
    private IReadOnlyList<ImageAttachment> _images = [];
    private int _index;
    private Bitmap? _bitmap;

    public ImagePreviewWindow()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this, "Image preview");
        Checkerboard.Background = FilePreviewWindow.CheckerboardBrush();
    }

    public static void Show(IReadOnlyList<ImageAttachment> images, int startIndex, Window owner) =>
        Build(images, startIndex).Show(owner);

    // The render harness's own step (the same split FilePreviewWindow.Build/ScreenshotPreviewWindow.Build make):
    // the window built and showing its first image, without being put on screen.
    internal static ImagePreviewWindow Build(IReadOnlyList<ImageAttachment> images, int startIndex)
    {
        var window = new ImagePreviewWindow { _images = images };
        window._ShowImage(Math.Clamp(startIndex, 0, Math.Max(0, images.Count - 1)));
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

    private void _OnFit(object? sender, RoutedEventArgs e) => PreviewImage.Stretch = Stretch.Uniform;

    // 1:1: the image keeps its own pixel size and the surrounding ScrollViewer picks up scrollbars once it no
    // longer fits — no pan/deep-zoom (out of v1 scope), just the plain "actual size" the ticket asks for.
    private void _OnActualSize(object? sender, RoutedEventArgs e) => PreviewImage.Stretch = Stretch.None;

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
        CountText.Text = _images.Count == 1 ? "Image" : $"Image {index + 1} of {_images.Count}";
        NavigationRow.IsVisible = _images.Count > 1;
        PreviousButton.IsEnabled = index > 0;
        NextButton.IsEnabled = index < _images.Count - 1;
    }
}
