using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Voice;
using FluentAssertions;

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

    [Fact]
    public async Task LoadAsync_NeverCalibratedOnThisMachine_ReturnsNull()
    {
        var store = new TranscriptionCalibrationStore(_configFilePath, "desktop-A");

        (await store.LoadAsync()).Should().BeNull();
    }

    [Fact]
    public async Task SaveThenLoad_OnTheSameMachine_RoundTrips()
    {
        var store = new TranscriptionCalibrationStore(_configFilePath, "desktop-A");
        var calibration = new TranscriptionCalibration(820, 3, VoiceBackendPreference.Cpu, "large-v3-turbo");

        await store.SaveAsync(calibration);

        (await store.LoadAsync()).Should().Be(calibration);
    }

    [Fact]
    public async Task ADifferentMachine_DoesNotSeeAnothersCalibration()
    {
        await new TranscriptionCalibrationStore(_configFilePath, "desktop-A")
            .SaveAsync(new TranscriptionCalibration(820, 3, VoiceBackendPreference.Cpu, "large-v3-turbo"));

        (await new TranscriptionCalibrationStore(_configFilePath, "laptop-B").LoadAsync()).Should().BeNull();
    }

    [Fact]
    public async Task SavingOneMachine_LeavesAnothersCalibrationIntact()
    {
        var desktop = new TranscriptionCalibrationStore(_configFilePath, "desktop-A");
        var laptop = new TranscriptionCalibrationStore(_configFilePath, "laptop-B");
        var desktopResult = new TranscriptionCalibration(820, 3, VoiceBackendPreference.Cpu, "large-v3-turbo");

        await desktop.SaveAsync(desktopResult);
        await laptop.SaveAsync(new TranscriptionCalibration(400, 41, VoiceBackendPreference.Vulkan, "small"));

        (await desktop.LoadAsync()).Should().Be(desktopResult, "the laptop's save must not clobber the desktop's entry");
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
