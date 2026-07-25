using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>Test double for <see cref="IScreenshotCapture"/>: stands in for the OS picker with a fixed answer — an image, a cancel (null), or a failure.</summary>
internal sealed class FakeScreenshotCapture : IScreenshotCapture
{
    public bool IsSupported { get; init; } = true;

    /// <summary>What the picker "returns". Null stands for the operator cancelling.</summary>
    public byte[]? Result { get; init; }

    /// <summary>Set to make the capture itself fail — a portal that refuses, a helper process that cannot start.</summary>
    public Exception? Failure { get; init; }

    /// <summary>Runs while the picker is "open", so a test can act in the middle of a capture (pressing the key twice, say).</summary>
    public Func<Task>? WhileCapturing { get; init; }

    public int CaptureCallCount { get; private set; }

    public async Task<byte[]?> CaptureInteractiveAsync(CancellationToken cancellationToken = default)
    {
        CaptureCallCount++;

        if (WhileCapturing is not null)
        {
            await WhileCapturing();
        }

        return Failure is null ? Result : throw Failure;
    }
}
