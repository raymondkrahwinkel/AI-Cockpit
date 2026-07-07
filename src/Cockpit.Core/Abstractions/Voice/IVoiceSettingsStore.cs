using Cockpit.Core.Voice;

namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// Loads and persists <see cref="VoiceSettings"/> in <c>cockpit.json</c>. When no settings were ever
/// saved, <see cref="LoadAsync"/> returns the defaults (voice disabled).
/// </summary>
public interface IVoiceSettingsStore
{
    Task<VoiceSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(VoiceSettings settings, CancellationToken cancellationToken = default);
}
