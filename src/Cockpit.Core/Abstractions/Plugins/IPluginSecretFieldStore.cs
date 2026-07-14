namespace Cockpit.Core.Abstractions.Plugins;

/// <summary>
/// The storage keys the plugins keep a credential in, beyond the names the host recognises by itself.
/// <para>
/// The names are not secrets — knowing a plugin stores something under <c>pat</c> tells you nothing — and they
/// have to be readable <em>before</em> the settings are decrypted, since they are what says which fields to
/// decrypt. So they live in the clear in <c>cockpit.json</c>, next to the encrypted values they describe.
/// </para>
/// </summary>
public interface IPluginSecretFieldStore
{
    /// <summary>Every declared key, across all plugins. Read at startup, before anything reads a plugin's settings.</summary>
    Task<IReadOnlyList<string>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Remembers that <paramref name="key"/> holds a credential for <paramref name="pluginId"/>.</summary>
    Task DeclareAsync(string pluginId, IEnumerable<string> keys, CancellationToken cancellationToken = default);
}
