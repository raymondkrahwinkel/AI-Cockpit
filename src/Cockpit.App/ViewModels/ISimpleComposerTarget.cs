using Avalonia.Media.Imaging;

namespace Cockpit.App.ViewModels;

// AC-1528: what the Simple composer's paperclip and mic act on; the names are a session's own, so it needs no new code.
public interface ISimpleComposerTarget
{
    bool VoiceEnabled { get; }

    // Any image, not only a screenshot: null when it landed, otherwise why not.
    Task<string?> InjectScreenshotAsync(byte[] screenshotPng);

    void InjectText(string text);

    bool BeginVoiceHold();

    Task EndVoiceHoldAsync();
}

public static class SimpleComposerAttachment
{
    private static readonly HashSet<string> ImageExtensions =
        new([".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"], StringComparer.OrdinalIgnoreCase);

    // An image the target cannot take goes as a path too: the agent reads the file itself.
    public static async Task AttachAsync(ISimpleComposerTarget target, string path)
    {
        if (ImageExtensions.Contains(Path.GetExtension(path))
            && await target.InjectScreenshotAsync(_AsPng(await File.ReadAllBytesAsync(path))) is null)
        {
            return;
        }

        target.InjectText(path.Contains(' ') ? $"\"{path}\"" : path);
    }

    // The wire takes PNG only (`ImageAttachmentViewModel.MediaType`), so a photo is re-encoded rather than mislabelled.
    private static byte[] _AsPng(byte[] image)
    {
        using var input = new MemoryStream(image);
        using var bitmap = new Bitmap(input);
        using var output = new MemoryStream();
        bitmap.Save(output, PngBitmapEncoderOptions.Default);
        return output.ToArray();
    }
}
