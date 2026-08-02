namespace Cockpit.Plugin.YouTrack;

// Tells an image file's real media type from its bytes (AC-170) — never from its extension, which a caller
// could set to anything. Same idea as `Cockpit.Core.Screenshots.PngImage`'s signature read (this plugin
// does not reference `Cockpit.Core`, so the check is reimplemented here rather than shared), extended to
// the handful of formats YouTrack's attachment endpoint and Exclr8's paste handler actually produce.
internal static class ImageContentSniffer
{
    // Reads just enough of `path` to identify it as an image by its magic bytes, returning the
    // matching MIME type — or `false` when the content is not a recognized image format,
    // regardless of what the file's extension claims.
    public static bool TryDetectMediaType(string path, out string mediaType)
    {
        mediaType = string.Empty;

        byte[] header;
        try
        {
            using var stream = File.OpenRead(path);
            header = new byte[Math.Min(32, stream.Length)];
            var read = 0;
            while (read < header.Length)
            {
                var n = stream.Read(header, read, header.Length - read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return TryDetectMediaType(header, out mediaType);
    }

    // Signature-only variant of `TryDetectMediaType(string, out string)` for bytes already in memory (unit tests, in particular).
    public static bool TryDetectMediaType(ReadOnlySpan<byte> header, out string mediaType)
    {
        if (_StartsWith(header, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            mediaType = "image/png";
            return true;
        }

        if (_StartsWith(header, [0xFF, 0xD8, 0xFF]))
        {
            mediaType = "image/jpeg";
            return true;
        }

        if (_StartsWith(header, "GIF87a"u8) || _StartsWith(header, "GIF89a"u8))
        {
            mediaType = "image/gif";
            return true;
        }

        if (_StartsWith(header, "BM"u8))
        {
            mediaType = "image/bmp";
            return true;
        }

        // WEBP: RIFF <4-byte size> WEBP
        if (header.Length >= 12 && _StartsWith(header, "RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            mediaType = "image/webp";
            return true;
        }

        mediaType = string.Empty;
        return false;
    }

    private static bool _StartsWith(ReadOnlySpan<byte> header, ReadOnlySpan<byte> signature) =>
        header.Length >= signature.Length && header[..signature.Length].SequenceEqual(signature);
}
