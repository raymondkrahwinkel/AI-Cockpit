namespace Cockpit.Plugins.Abstractions.Files;

public interface IFileImageSource
{
    /// <summary>Reads a relative image for a document, with bounded bytes and no credential-bearing URLs.</summary>
    Task<byte[]> ReadImageAsync(FileRef document, string target, CancellationToken cancellationToken);
}

public sealed record FileImageRequest(FileRef File, string Target);

public static class FileImageChannel
{
    public const string Read = "files-image-read";
    public const int MaxBytes = 2 * 1024 * 1024;
}
