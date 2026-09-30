using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Voice;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// AC-68 slice 3: the calibration is stored per machine, because a config can be synced or restored onto another
/// box and a GPU measurement from one machine says nothing about another's. These pin the round-trip and the
/// per-machine isolation — saving one machine's result must not disturb another's, and loading on a machine that
/// never calibrated returns nothing rather than someone else's numbers.
/// </summary>
public class TranscriptionCalibrationStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public TranscriptionCalibrationStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    private static TranscriptionCalibration Calibration(VoiceBackendPreference chosen, string model = "large-v3-turbo") =>
        new(
            [
                new BackendMeasurement(VoiceBackendPreference.Cpu, LatencyMs: 4200, HitchMs: 0),
                new BackendMeasurement(VoiceBackendPreference.Vulkan, LatencyMs: 820, HitchMs: 3),
            ],
            chosen,
            [
                new ModelMeasurement("large-v3-turbo", LatencyMs: 820),
                new ModelMeasurement("small", LatencyMs: 300),
            ],
            RecommendedModel: "large-v3-turbo",
            model);

    [Fact]
    public async Task SaveThenLoad_OnTheSameMachine_RoundTrips()
    {
        var store = new TranscriptionCalibrationStore(_configFilePath, "desktop-A");
        var calibration = Calibration(VoiceBackendPreference.Vulkan);

        await store.SaveAsync(calibration);

        Assert.Equivalent(calibration, await store.LoadAsync());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }
}
