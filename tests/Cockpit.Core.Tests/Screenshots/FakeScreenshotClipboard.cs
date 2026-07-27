using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>Test double for <see cref="IScreenshotClipboard"/>: records what was written and can refuse, the way a clipboard another application holds locked does.</summary>
internal sealed class FakeScreenshotClipboard : IScreenshotClipboard
{
    /// <summary>Set false to stand in for a clipboard that will not take the image.</summary>
    public bool AcceptsWrites { get; init; } = true;

    /// <summary>What was put on the clipboard, in order.</summary>
    public List<byte[]> Written { get; } = [];

    public Task<bool> TrySetImageAsync(byte[] png, CancellationToken cancellationToken = default)
    {
        if (!AcceptsWrites)
        {
            return Task.FromResult(false);
        }

        Written.Add(png);
        return Task.FromResult(true);
    }
}
