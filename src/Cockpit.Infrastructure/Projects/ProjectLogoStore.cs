using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Projects;

/// <summary>
/// Stores project logos as files under <c>project-logos/</c> next to <c>cockpit.json</c>, one per project, named
/// after the project id so a project can only ever have one and removing it needs no bookkeeping.
/// </summary>
internal sealed class ProjectLogoStore(HttpClient httpClient, ILogger<ProjectLogoStore>? logger = null)
    : IProjectLogoStore, ISingletonService
{
    /// <summary>A logo is a small image; anything past this is not one, and downloading it would be someone else's file transfer.</summary>
    private const int MaxBytes = 8 * 1024 * 1024;

    private static readonly string[] _ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".ico"];

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

            Remove(projectId);
            Directory.CreateDirectory(CockpitConfigPath.ProjectLogosRoot);
            var path = Path.Combine(CockpitConfigPath.ProjectLogosRoot, projectId + extension);
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
        && path.StartsWith(CockpitConfigPath.ProjectLogosRoot, StringComparison.Ordinal);

    public void Remove(string projectId)
    {
        if (!Directory.Exists(CockpitConfigPath.ProjectLogosRoot))
        {
            return;
        }

        // Every extension it could have been stored under: the project keeps one logo, whatever kind of image it is.
        foreach (var existing in Directory.EnumerateFiles(CockpitConfigPath.ProjectLogosRoot, projectId + ".*"))
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

    /// <summary>The source's extension when it looks like an image, else <c>.png</c> — the stored name only has to be stable and unique, and every renderer here sniffs the bytes rather than trusting the name.</summary>
    private static string _ExtensionOf(string source)
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        return Array.IndexOf(_ImageExtensions, extension) >= 0 ? extension : ".png";
    }
}
