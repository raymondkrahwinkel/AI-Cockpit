using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of a <see cref="TranscriptionCalibration"/> (AC-68 slice 3), stored per machine name
/// under the <c>transcriptionCalibrations</c> section of <c>cockpit.json</c>.</summary>
internal sealed class TranscriptionCalibrationEntry
{
    public double LatencyMs { get; set; }

    public double HitchMs { get; set; }

    public VoiceBackendPreference Backend { get; set; }

    public string Model { get; set; } = "large-v3-turbo";

    public static TranscriptionCalibrationEntry FromDomain(TranscriptionCalibration calibration) => new()
    {
        LatencyMs = calibration.LatencyMs,
        HitchMs = calibration.HitchMs,
        Backend = calibration.Backend,
        Model = calibration.Model,
    };

    public TranscriptionCalibration ToDomain() => new(LatencyMs, HitchMs, Backend, Model);
}
