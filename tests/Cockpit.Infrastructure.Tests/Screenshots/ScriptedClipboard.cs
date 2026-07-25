using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Tests.Screenshots;

/// <summary>
/// Test double for <see cref="IScreenshotClipboard"/>: hands back the scripted contents one read at a time —
/// the first read is the "before" the capture compares against, the rest are the poll. Once the script runs out
/// it keeps returning the last entry, so a test says how the clipboard changes rather than how often it is read.
/// </summary>
internal sealed class ScriptedClipboard(params byte[]?[] reads) : IScreenshotClipboard
{
    private int _read;

    public Task<byte[]?> TryReadImageAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_read < reads.Length ? reads[_read++] : reads[^1]);

    /// <summary>The Windows capture only ever reads; writing is the terminal route's, and no test here drives it.</summary>
    public Task<bool> TrySetImageAsync(byte[] png, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The Windows capture does not write to the clipboard.");
}
