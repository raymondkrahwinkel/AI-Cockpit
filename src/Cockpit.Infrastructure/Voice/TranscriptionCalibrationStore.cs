using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Voice;

// Persists the STT calibration under the `transcriptionCalibrations` section of `cockpit.json`,
// keyed by machine name (AC-68 slice 3). Read-modify-writes the whole file via `CockpitConfigFileAccess`
// so a save touches only this machine's entry — a synced config keeps every other machine's measurement.
internal sealed class TranscriptionCalibrationStore : ITranscriptionCalibrationStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;
    private readonly string _machineKey;

    public TranscriptionCalibrationStore()
        : this(CockpitConfigPath.Default, _DefaultMachineKey())
    {
    }

    // Test seam: point the store at an arbitrary config file path and machine key.
    internal TranscriptionCalibrationStore(string configFilePath, string machineKey)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
        _machineKey = machineKey;
    }

    public async Task<TranscriptionCalibration?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.TranscriptionCalibrations is { } calibrations && calibrations.TryGetValue(_machineKey, out var entry)
            ? entry.ToDomain()
            : null;
    }

    public Task SaveAsync(TranscriptionCalibration calibration, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.TranscriptionCalibrations[_machineKey] = TranscriptionCalibrationEntry.FromDomain(calibration),
            cancellationToken);

    private static string _DefaultMachineKey()
    {
        try
        {
            return string.IsNullOrWhiteSpace(Environment.MachineName) ? "default" : Environment.MachineName;
        }
        catch
        {
            return "default";
        }
    }
}
