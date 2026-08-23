namespace Cockpit.Core.Abstractions.Plugins;

/// <summary>
/// The storage keys plugins keep a credential in, beyond the names the host recognises itself. The names aren't
/// secrets — knowing a plugin stores something under <c>pat</c> tells you nothing — and must be readable
/// <em>before</em> settings decrypt, since they say what to decrypt; hence live in the clear in <c>cockpit.json</c>.
/// </summary>
public interface IPluginSecretFieldStore
{
    /// <summary>
    /// Every declared key, across all plugins. Read at startup, before anything reads a plugin's settings.
    /// </summary>
    Task<IReadOnlyList<string>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Remembers that <paramref name="key"/> holds a credential for <paramref name="pluginId"/>.
    /// </summary>
    Task DeclareAsync(string pluginId, IEnumerable<string> keys, CancellationToken cancellationToken = default);
}
