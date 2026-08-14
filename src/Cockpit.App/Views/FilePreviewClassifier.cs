using System.Text;

namespace Cockpit.App.Views;

// The soort-choice a resolved path gets, pure and disk-free so it is testable without a window
// (AC-642). Directory/Missing are decided by the caller's own File.Exists/Directory.Exists — a one-line check
// that needs no test of its own — so this only covers the shapes a regular, readable file can take.
internal static class FilePreviewClassifier
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];

    public static FilePreviewKind Classify(string path, ReadOnlySpan<byte> head)
    {
        var extension = Path.GetExtension(path);
        if (Array.Exists(ImageExtensions, ext => ext.Equals(extension, StringComparison.OrdinalIgnoreCase)))
        {
            return FilePreviewKind.Image;
        }

        if (extension.Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return FilePreviewKind.Svg;
        }

        if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
        {
            return FilePreviewKind.Markdown;
        }

        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            return FilePreviewKind.Json;
        }

        if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase))
        {
            return FilePreviewKind.Csv;
        }

        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return FilePreviewKind.Pdf;
        }

        return LooksLikeText(head) ? FilePreviewKind.Text : FilePreviewKind.Other;
    }

    // No extension list for text — a list like that only ever trails behind (`.axaml`, `.csproj`, `.mdc`, ...).
    // A NUL byte rules text out outright; what remains must decode as strict UTF-8.
    public static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        if (head.IndexOf((byte)0) >= 0)
        {
            return false;
        }

        try
        {
            _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(head);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
