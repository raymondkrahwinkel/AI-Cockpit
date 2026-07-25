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
}
