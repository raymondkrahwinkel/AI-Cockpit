using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>Test double for <see cref="IScreenshotCapture"/>: stands in for the OS picker with a fixed answer — a capture, a cancel (null), or a failure.</summary>
internal sealed class FakeScreenshotCapture : IScreenshotCapture
{
    public bool IsSupported { get; set; } = true;

    /// <summary>Left unfinished to stand in for a platform that has not said yet whether it can capture — Linux, waiting on the session bus.</summary>
    public Task SupportSettled { get; init; } = Task.CompletedTask;

    /// <summary>What the capture "returns". Null stands for the operator cancelling.</summary>
    public ScreenCapture? Result { get; init; }

    /// <summary>Set to make the capture itself fail — a portal that refuses, a helper process that cannot start.</summary>
    public Exception? Failure { get; init; }

    /// <summary>Runs while the picker is "open", so a test can act in the middle of a capture (pressing the key twice, say).</summary>
    public Func<Task>? WhileCapturing { get; init; }

    public int CaptureCallCount { get; private set; }

    public async Task<ScreenCapture?> CaptureAsync(CancellationToken cancellationToken = default)
    {
        CaptureCallCount++;

        if (WhileCapturing is not null)
        {
            await WhileCapturing();
        }

        return Failure is null ? Result : throw Failure;
    }
}
