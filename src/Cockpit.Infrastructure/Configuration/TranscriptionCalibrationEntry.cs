using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of a <see cref="TranscriptionCalibration"/> (AC-68), stored per machine name under the
/// <c>transcriptionCalibrations</c> section of <c>cockpit.json</c>. Holds the per-backend measurements and the
/// backend the verdict chose from them.</summary>
internal sealed class TranscriptionCalibrationEntry
{
    public List<BackendMeasurementEntry> Measurements { get; set; } = [];

    public VoiceBackendPreference ChosenBackend { get; set; }

    public List<ModelMeasurementEntry> ModelLadder { get; set; } = [];

    public string RecommendedModel { get; set; } = "large-v3-turbo";

    public string Model { get; set; } = "large-v3-turbo";

    public static TranscriptionCalibrationEntry FromDomain(TranscriptionCalibration calibration) => new()
    {
        Measurements = calibration.Measurements.Select(BackendMeasurementEntry.FromDomain).ToList(),
        ChosenBackend = calibration.ChosenBackend,
        ModelLadder = calibration.ModelLadder.Select(ModelMeasurementEntry.FromDomain).ToList(),
        RecommendedModel = calibration.RecommendedModel,
        Model = calibration.Model,
    };

    public TranscriptionCalibration ToDomain() => new(
        Measurements.Select(measurement => measurement.ToDomain()).ToList(),
        ChosenBackend,
        ModelLadder.Select(measurement => measurement.ToDomain()).ToList(),
        RecommendedModel,
        Model);
}

/// <summary>On-disk shape of one <see cref="ModelMeasurement"/> (AC-68).</summary>
internal sealed class ModelMeasurementEntry
{
    public string Model { get; set; } = "large-v3-turbo";

    public double LatencyMs { get; set; }

    public static ModelMeasurementEntry FromDomain(ModelMeasurement measurement) => new()
    {
        Model = measurement.Model,
        LatencyMs = measurement.LatencyMs,
    };

    public ModelMeasurement ToDomain() => new(Model, LatencyMs);
}

/// <summary>On-disk shape of one <see cref="BackendMeasurement"/> (AC-68).</summary>
internal sealed class BackendMeasurementEntry
{
    public VoiceBackendPreference Backend { get; set; }

    public double LatencyMs { get; set; }

    public double HitchMs { get; set; }

    public static BackendMeasurementEntry FromDomain(BackendMeasurement measurement) => new()
    {
        Backend = measurement.Backend,
        LatencyMs = measurement.LatencyMs,
        HitchMs = measurement.HitchMs,
    };

    public BackendMeasurement ToDomain() => new(Backend, LatencyMs, HitchMs);
}
