using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cockpit.Plugins.Abstractions.Files;
using SkiaSharp;

namespace Cockpit.App.Views.Editor;

public sealed partial class MarkdownEditor
{
    private static readonly Regex ImageLine = new(@"^\s*!\[([^\]]*)\]\(([^\r\n]+)\)\s*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly SemaphoreSlim ImageDecoders = new(2);
    private bool _imagesAttached;
    private readonly Dictionary<string, Task<Bitmap>> _images = [];
    private readonly List<(string Target, WeakReference<ContentControl> Control)> _imageControls = [];
    private CancellationTokenSource _imageLifetime = new();
    private Func<string, CancellationToken, Task<byte[]>>? _imageLoader;

    public Func<string, CancellationToken, Task<byte[]>>? ImageLoader
    {
        get => _imageLoader;
        set
        {
            _imageLoader = value;
            InvalidateImages();
        }
    }

    public void InvalidateImages()
    {
        _imageLifetime.Cancel();
        _imageLifetime.Dispose();
        _imageLifetime = new CancellationTokenSource();
        foreach (var reference in _imageControls)
        {
            if (reference.Control.TryGetTarget(out var control))
            {
                control.Content = null;
            }
        }
        _imageControls.Clear();
        foreach (var task in _images.Values)
        {
            _ = task.ContinueWith(completed =>
            {
                if (completed.IsCompletedSuccessfully)
                {
                    completed.Result.Dispose();
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        _images.Clear();
        if (_imagesAttached)
        {
            _Redraw();
        }
    }

    private Control _Image(string target, string alt)
    {
        var control = new ContentControl { Content = new TextBlock { Text = alt.Length > 0 ? alt : "Loading image…", Foreground = TextSecondary } };
        _imageControls.RemoveAll(reference => !reference.Control.TryGetTarget(out _));
        _imageControls.Add((target, new WeakReference<ContentControl>(control)));
        if (!_images.TryGetValue(target, out var task))
        {
            if (_images.Count >= 64 || ImageLoader is not { } loader)
            {
                control.Content = new TextBlock { Text = alt.Length > 0 ? alt : "Image preview limit reached", Foreground = TextSecondary };
                return control;
            }
            task = _DecodeImageAsync(loader, target, _imageLifetime.Token);
            _images[target] = task;
        }
        _ = _ShowImageAsync(control, task, target, alt, _imageLifetime.Token);
        return control;
    }

    private async Task _ShowImageAsync(ContentControl control, Task<Bitmap> load, string target, string alt, CancellationToken ct)
    {
        var firstLoad = !load.IsCompleted;
        try
        {
            var bitmap = await load.ConfigureAwait(true);
            if (!ct.IsCancellationRequested && _images.GetValueOrDefault(target) == load)
            {
                // Each downsampled bitmap is at most 4 MiB; release old previews before exceeding 32 MiB.
                while (_images.Count(entry => entry.Value.IsCompletedSuccessfully) > 8)
                {
                    var oldest = _images.First(entry => entry.Key != target && entry.Value.IsCompletedSuccessfully);
                    foreach (var reference in _imageControls.Where(reference => reference.Target == oldest.Key))
                    {
                        if (reference.Control.TryGetTarget(out var previous))
                        {
                            previous.Content = new TextBlock { Text = "Image preview released", Foreground = TextSecondary };
                        }
                    }
                    _images.Remove(oldest.Key);
                    oldest.Value.Result.Dispose();
                }
                control.Content = new Image { Source = bitmap, MaxWidth = 640, MaxHeight = 480,
                    Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
                Avalonia.Automation.AutomationProperties.SetName(control, alt);
                if (firstLoad && _imagesAttached)
                {
                    _Redraw();
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!ct.IsCancellationRequested)
            {
                control.Content = new TextBlock { Text = alt.Length > 0 ? alt : "Image unavailable", Foreground = TextSecondary };
            }
        }
    }

    private static async Task<Bitmap> _DecodeImageAsync(Func<string, CancellationToken, Task<byte[]>> loader, string target, CancellationToken ct)
    {
        var bytes = await loader(target, ct).ConfigureAwait(false);
        await ImageDecoders.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => _DecodeImage(bytes, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            ImageDecoders.Release();
        }
    }

    private static Bitmap _DecodeImage(byte[] bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (bytes.Length > FileImageChannel.MaxBytes)
        {
            throw new ArgumentException("This image is too large.");
        }
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Gif or SKEncodedImageFormat.Webp)
            || codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > 16_000_000)
        {
            throw new ArgumentException("This image format or dimensions are unsupported.");
        }
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = codec.Info.Width >= codec.Info.Height
            ? Bitmap.DecodeToWidth(stream, Math.Min(1024, codec.Info.Width))
            : Bitmap.DecodeToHeight(stream, Math.Min(1024, codec.Info.Height));
        if (ct.IsCancellationRequested)
        {
            bitmap.Dispose();
            ct.ThrowIfCancellationRequested();
        }
        return bitmap;
    }
}
