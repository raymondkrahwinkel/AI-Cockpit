using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Svg.Skia;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Projects;

// Stores project logos as files under `project-logos/` next to `cockpit.json`, one per project, named
// after the project id so a project can only ever have one and removing it needs no bookkeeping.
internal sealed class ProjectLogoStore(HttpClient httpClient, ILogger<ProjectLogoStore>? logger = null, string? root = null)
    : IProjectLogoStore, ISingletonService
{
    // Where the copies live. Overridable so a test writes to its own folder rather than the operator's config directory.
    private string _Root => root ?? CockpitConfigPath.ProjectLogosRoot;

    // A logo is a small image; anything past this is not one, and downloading it would be someone else's file transfer.
    private const int MaxBytes = 8 * 1024 * 1024;

    // How large a rasterised SVG is stored, on its longest side: comfortably past the 34px card well and the dialog's preview on a high-DPI screen, and still a small file.
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
            var path = Path.Combine(_Root, _FileKey(projectId) + extension);
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

    // On the separator, not on the bare prefix: a sibling folder whose name merely starts with the same text
    // (project-logos-backup beside project-logos) is not inside the store, and treating it as the stored copy
    // would leave the logo pointing at a file the cockpit neither owns nor removes with its project.
    public bool IsStoredCopy(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.StartsWith(_Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    public void Remove(string projectId)
    {
        if (!Directory.Exists(_Root))
        {
            return;
        }

        // Matched on the file's own name rather than by handing the id to a search pattern: an id is data from
        // cockpit.json, and a pattern built from it ("../../notes/*") enumerates — and would delete — files well
        // outside this folder.
        var key = _FileKey(projectId);
        foreach (var existing in Directory.EnumerateFiles(_Root)
            .Where(path => string.Equals(Path.GetFileNameWithoutExtension(path), key, StringComparison.Ordinal)))
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

        // Read with a ceiling rather than buffering whatever arrives: a chunked response carries no Content-Length,
        // so the header check above cannot see its size and the whole thing would land in memory before anyone
        // measured it.
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    private static async Task<byte[]?> _ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length is 0 or > MaxBytes)
        {
            return null;
        }

        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    // Whether these bytes are an SVG: by extension, or by what the document actually starts with — a URL that serves one need not end in `.svg`.
    private static bool _IsSvg(byte[] bytes, string extension)
    {
        if (string.Equals(extension, ".svg", StringComparison.Ordinal))
        {
            return true;
        }

        var start = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 512));
        return start.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    // The SVG drawn onto a PNG at `RasterSize` on its longest side, transparent behind it. Null when
    // the document does not parse or draws nothing, which leaves the card on the project's initial rather than on
    // an empty square.
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

    // The file name this project's logo is stored under: its id when that is already nothing but letters, digits,
    // dashes and underscores — which is what `Cockpit.Core.Projects.Project.Create` mints — and
    // otherwise a hash of it. An id is data off disk, and a hand-written or shared `cockpit.json` can put
    // anything in it, including a path that climbs out of this folder. Hashing keeps such an id usable (the
    // project still gets its logo) without ever letting it decide where the file lands.
    private static string _FileKey(string projectId)
    {
        var safe = projectId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
        if (safe && projectId.Length <= 64)
        {
            return projectId;
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(projectId)))[..32];
    }

    // The source's extension when it looks like an image, else `.png` — the stored name only has to be stable and unique, and every renderer here sniffs the bytes rather than trusting the name.
    private static string _ExtensionOf(string source)
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        return ProjectLogoFormats.Extensions.Contains(extension) ? extension : ".png";
    }
}
