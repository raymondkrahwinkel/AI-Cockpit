namespace Cockpit.Plugins.Abstractions;

/// <summary>Per-plugin key/value storage, persisted in a plugin-scoped section of the host's <c>cockpit.json</c>. Values are serialized as JSON.</summary>
public interface IPluginStorage
{
    T? Get<T>(string key);

    void Set<T>(string key, T value);

    /// <summary>
    /// Stores a credential: a token, an API key, a webhook URL — anything that would be a problem in someone
    /// else's hands.
    /// <para>
    /// The host recognises the usual names on its own (<c>token</c>, <c>apiKey</c>, <c>secret</c>, <c>password</c>,
    /// <c>webhook</c>), so a plugin that calls plain <see cref="Set{T}"/> for a field with one of those names is
    /// already covered. This is for the ones it cannot guess — a <c>pat</c>, a <c>credential</c> — and for saying
    /// so at the call site, where the plugin author is the one who knows. What is stored this way is encrypted at
    /// rest whenever the operator has turned that on, and is emptied from a backup that says it carries no
    /// credentials.
    /// </para>
    /// <para>
    /// A plugin can also declare the keys in its <c>plugin.json</c> (<c>"secretKeys": ["pat"]</c>), which is what
    /// covers values written before this existed — and lets the store show, at install time, which credentials a
    /// plugin intends to keep.
    /// </para>
    /// </summary>
    /// <remarks>
    /// A default implementation, so this is an addition to the contract rather than a break of it: an existing
    /// plugin (or a test double) that implements <see cref="IPluginStorage"/> keeps compiling, and simply stores
    /// the value as it always did. The host's own implementation overrides it and does the declaring.
    /// </remarks>
    void SetSecret(string key, string value) => Set(key, value);

    /// <summary>Reads back what <see cref="SetSecret"/> stored. The same as <see cref="Get{T}"/> for a string — the difference is at the writing end, where the host has to be told what it is looking at.</summary>
    string? GetSecret(string key) => Get<string>(key);
}
