using Cockpit.Core.TranscriptDisplay;

namespace Cockpit.Core.Abstractions.TranscriptDisplay;

/// <summary>
/// Loads and persists <see cref="TranscriptDisplaySettings"/> in <c>cockpit.json</c> (the same config
/// file the profiles and notifications live in). When no settings were ever saved,
/// <see cref="LoadAsync"/> returns the defaults.
/// </summary>
public interface ITranscriptDisplaySettingsStore
{
    Task<TranscriptDisplaySettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(TranscriptDisplaySettings settings, CancellationToken cancellationToken = default);
}
