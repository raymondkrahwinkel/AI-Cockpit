using Cockpit.Core.Assistant;

namespace Cockpit.Core.Abstractions.Assistant;

/// <summary>
/// Loads and persists <see cref="AssistantSettings"/> in <c>cockpit.json</c>. When no settings were ever
/// saved, <see cref="LoadAsync"/> returns the defaults — <see cref="AssistantSettings.IsEnabled"/> false,
/// so a fresh install never spins up an instance or loads a model on its own.
/// </summary>
public interface IAssistantSettingsStore
{
    Task<AssistantSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AssistantSettings settings, CancellationToken cancellationToken = default);
}
