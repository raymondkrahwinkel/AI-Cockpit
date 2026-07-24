using Microsoft.Extensions.Logging;
using SkiaSharp;
using Svg.Skia;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Projects;

/// <summary>
/// Stores project logos as files under <c>project-logos/</c> next to <c>cockpit.json</c>, one per project, named
/// after the project id so a project can only ever have one and removing it needs no bookkeeping.
/// </summary>
internal sealed class ProjectLogoStore(HttpClient httpClient, ILogger<ProjectLogoStore>? logger = null, string? root = null)
    : IProjectLogoStore, ISingletonService
{
    /// <summary>Where the copies live. Overridable so a test writes to its own folder rather than the operator's config directory.</summary>
    private string _Root => root ?? CockpitConfigPath.ProjectLogosRoot;

    /// <summary>A logo is a small image; anything past this is not one, and downloading it would be someone else's file transfer.</summary>
    private const int MaxBytes = 8 * 1024 * 1024;

    private static readonly string[] _ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".ico", ".svg"];

    /// <summary>How large a rasterised SVG is stored, on its longest side: comfortably past the 34px card well and the dialog's preview on a high-DPI screen, and still a small file.</summary>
    private const float RasterSize = 256f;

    public async Task<string?> SaveAsync(string projectId, string source, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        try
        {
            var trimmed = source.Trim();
            var (bytes, extension) = Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                ? (await _DownloadAsync(uri, cancellationToken).ConfigureAwait(false), _ExtensionOf(uri.AbsolutePath))
                : (await _ReadFileAsync(trimmed, cancellationToken).ConfigureAwait(false), _ExtensionOf(trimmed));

            if (bytes is null)
            {
                return null;
            }

            // An SVG is stored as the PNG it draws to. A logo is very often a vector — a company's own is almost
            // always one — but the surfaces that show it take a decoded bitmap, so converting here is what makes a
            // link to an .svg work at all rather than quietly falling back to the project's initial.
            if (_IsSvg(bytes, extension) && _RasterisedSvg(bytes) is { } raster)
            {
                (bytes, extension) = (raster, ".png");
            }

            Remove(projectId);
            Directory.CreateDirectory(_Root);
            var path = Path.Combine(_Root, projectId + extension);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch (Exception exception)
        {
            // A logo is decoration: an unreachable URL, a file that vanished between picking and saving, or a
            // read-only disk costs the picture. Failing the whole save over it would cost the project.
            logger?.LogWarning(exception, "Could not store a logo for project {ProjectId} from {Source}.", projectId, source);
            return null;
        }
    }

    public bool IsStoredCopy(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.StartsWith(_Root, StringComparison.Ordinal);

    public void Remove(string projectId)
    {
        if (!Directory.Exists(_Root))
        {
            return;
        }

        // Every extension it could have been stored under: the project keeps one logo, whatever kind of image it is.
        foreach (var existing in Directory.EnumerateFiles(_Root, projectId + ".*"))
        {
            try
            {
                File.Delete(existing);
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "Could not remove the stored logo {Path}.", existing);
            }
        }
    }

    private async Task<byte[]?> _DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes)
        {
            return null;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return bytes.Length is 0 or > MaxBytes ? null : bytes;
    }

    private static async Task<byte[]?> _ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length is 0 or > MaxBytes)
        {
            return null;
        }

        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether these bytes are an SVG: by extension, or by what the document actually starts with — a URL that serves one need not end in <c>.svg</c>.</summary>
    private static bool _IsSvg(byte[] bytes, string extension)
    {
        if (string.Equals(extension, ".svg", StringComparison.Ordinal))
        {
            return true;
        }

        var start = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 512));
        return start.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The SVG drawn onto a PNG at <see cref="RasterSize"/> on its longest side, transparent behind it. Null when
    /// the document does not parse or draws nothing, which leaves the card on the project's initial rather than on
    /// an empty square.
    /// </summary>
    private static byte[]? _RasterisedSvg(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var svg = new SKSvg();
        if (svg.Load(stream) is not { } picture || picture.CullRect is { Width: <= 0 } or { Height: <= 0 })
        {
            return null;
        }

        var source = picture.CullRect;
        var scale = RasterSize / Math.Max(source.Width, source.Height);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale(scale);

        // Drawn from the picture's own origin: an SVG whose contents start away from (0,0) would otherwise be
        // rendered partly outside the surface.
        surface.Canvas.Translate(-source.Left, -source.Top);
        surface.Canvas.DrawPicture(picture);
        surface.Canvas.Flush();

        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded?.ToArray();
    }

    /// <summary>The source's extension when it looks like an image, else <c>.png</c> — the stored name only has to be stable and unique, and every renderer here sniffs the bytes rather than trusting the name.</summary>
    private static string _ExtensionOf(string source)
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        return Array.IndexOf(_ImageExtensions, extension) >= 0 ? extension : ".png";
    }
}
