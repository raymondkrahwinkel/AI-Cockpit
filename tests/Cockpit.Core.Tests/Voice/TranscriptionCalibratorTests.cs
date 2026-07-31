using System.Reflection;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Infrastructure.Voice;
using NSubstitute;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// The calibration child's stderr tail (AC-534): <see cref="_LogChildFailureIfAny"/> is the piece of
/// <c>_RunChildAsync</c> that decides whether a failed calibration child gets a diagnostic — the surrounding
/// method spawns this same executable in a headless calibration mode, which is not something a unit test should
/// do, so this is reached via reflection in isolation.
/// </summary>
public class TranscriptionCalibratorTests
{
    [Fact]
    public void LogChildFailureIfAny_NonZeroExitWithStderr_LogsTheTail()
    {
        var (calibrator, logger) = _CreateCalibrator();
        var stderrTail = new ProcessStderrTail();
        stderrTail.OnLine("libcudart.so: cannot open shared object file");

        _Invoke(calibrator, exitCode: 1, label: "GPU", stderrTail);

        var message = Assert.Single(logger.Messages);
        Assert.Contains("libcudart.so", message, StringComparison.Ordinal);
        Assert.Contains("GPU", message, StringComparison.Ordinal);
    }

    [Fact]
    public void LogChildFailureIfAny_ZeroExit_LogsNothing_EvenWithStderr()
    {
        var (calibrator, logger) = _CreateCalibrator();
        var stderrTail = new ProcessStderrTail();
        stderrTail.OnLine("routine chatter");

        _Invoke(calibrator, exitCode: 0, label: "CPU", stderrTail);

        Assert.Empty(logger.Messages);
    }

    [Fact]
    public void LogChildFailureIfAny_NonZeroExitButNoStderrCaptured_LogsNothing()
    {
        var (calibrator, logger) = _CreateCalibrator();

        _Invoke(calibrator, exitCode: 1, label: "GPU", new ProcessStderrTail());

        Assert.Empty(logger.Messages);
    }

    private static (TranscriptionCalibrator Calibrator, CapturingLogger<TranscriptionCalibrator> Logger) _CreateCalibrator()
    {
        var logger = new CapturingLogger<TranscriptionCalibrator>();
        var calibrator = new TranscriptionCalibrator(
            Substitute.For<ITranscriptionAdvisor>(),
            Substitute.For<IUiHitchProbe>(),
            Substitute.For<ITranscriptionCalibrationStore>(),
            Substitute.For<IVoiceSettingsStore>(),
            logger);
        return (calibrator, logger);
    }

    private static void _Invoke(TranscriptionCalibrator calibrator, int exitCode, string label, ProcessStderrTail stderrTail) =>
        calibrator.GetType()
            .GetMethod("_LogChildFailureIfAny", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(calibrator, [exitCode, label, stderrTail]);
}
