using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of a `TranscriptionCalibration` (AC-68), stored per machine name under the
// `transcriptionCalibrations` section of `cockpit.json`. Holds the per-backend measurements and the
// backend the verdict chose from them.
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

// On-disk shape of one `ModelMeasurement` (AC-68).
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

// On-disk shape of one `BackendMeasurement` (AC-68).
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
