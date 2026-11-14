namespace Cockpit.Plugins.Abstractions.Vault;

/// <summary>
/// A secret store a plugin connects to the host's vault hub (AC-2333), registered with <see cref="ICockpitHost.AddVaultProvider"/>.
/// </summary>
public interface IVaultProvider
{
    /// <summary>
    /// The provider's id, e.g. <c>1password</c> or <c>bitwarden</c>.
    /// </summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>
    /// The scheme its references start with, e.g. <c>op</c> or <c>bws</c>.
    /// </summary>
    string ReferenceScheme { get; }

    bool CanWrite => false;

    /// <summary>Returns the stable containing item identity and its current opaque revision, without a value.</summary>
    Task<VaultItemVersion> GetVersionAsync(string reference, CancellationToken cancellationToken) =>
        Task.FromException<VaultItemVersion>(new NotSupportedException("Revision checks are not supported."));

    /// <summary>Creates a concealed value, or updates the field at reference in place; never deletes or returns a value.</summary>
    Task WriteAsync(string name, string? reference, string value, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("Agent writes are disabled."));

    /// <summary>Updates only if the containing item's revision still matches, checked inside the provider's write gate.</summary>
    Task WriteAsync(string name, string reference, string value, VaultItemVersion expected, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("Revision-checked writes are not supported."));

    /// <summary>Sets named fields of one item in a single provider update; preserves other fields and never returns values.</summary>
    Task WriteFieldsAsync(string name, string? reference, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("Multi-field writes are not supported."));

    /// <summary>Updates several fields only while the containing item's revision still matches the supplied snapshot.</summary>
    Task WriteFieldsAsync(string name, string reference, IReadOnlyDictionary<string, string> fields, VaultItemVersion expected, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("Revision-checked writes are not supported."));

    Task<VaultProviderStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The items it offers, as names and references only — never a value.
    /// </summary>
    Task<IReadOnlyList<VaultItem>> ListItemsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The value behind <paramref name="reference"/>. Only the hub calls this.
    /// </summary>
    Task<string> ResolveAsync(string reference, CancellationToken cancellationToken);
}
