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

    /// <summary>
    /// Applies <paramref name="change"/> to what is stored in one read-modify-write against every other writer of the
    /// file, so a field the change leaves alone keeps what another writer saved meanwhile (AC-1475).
    /// </summary>
    /// <remarks>
    /// Default-implemented as a load and a save, not atomic, so the test doubles that never update stay as they are;
    /// the real store overrides it with one write under the config file's own gate.
    /// </remarks>
    async Task UpdateAsync(Func<AssistantSettings, AssistantSettings> change, CancellationToken cancellationToken = default) =>
        await SaveAsync(change(await LoadAsync(cancellationToken).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
}
