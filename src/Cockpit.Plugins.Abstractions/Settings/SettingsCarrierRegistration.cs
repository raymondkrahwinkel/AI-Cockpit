namespace Cockpit.Plugins.Abstractions.Settings;

/// <summary>
/// Somewhere a plugin can keep the cockpit's own portable settings for the operator (AC-540), so a fresh install
/// can take them over. The blob is opaque to the carrier: the host decides what goes in and reads it back itself.
/// </summary>
/// <param name="Id">A stable id for this carrier, unique among the carriers one host knows.</param>
/// <param name="DisplayName">What the operator sees this carrier called.</param>
/// <param name="AddressLabel">Where the settings are kept, as the operator should read it — an address, an account.</param>
/// <param name="HasStoredSettingsAsync">Whether the carrier holds settings for this operator right now.</param>
/// <param name="ReadAsync">The blob last written, or null when the carrier holds none.</param>
/// <param name="WriteAsync">Keeps the blob, replacing whatever the carrier held before.</param>
public sealed record SettingsCarrierRegistration(
    string Id,
    string DisplayName,
    string AddressLabel,
    Func<CancellationToken, Task<bool>> HasStoredSettingsAsync,
    Func<CancellationToken, Task<byte[]?>> ReadAsync,
    Func<byte[], CancellationToken, Task> WriteAsync)
{
    /// <summary>
    /// Keeps the blob only when the carrier still holds what the host last read (AC-1510), so a write from another
    /// machine in between is never overwritten. The base version is the lowercase hex SHA-256 of the blob last read,
    /// or null when the carrier held none. Returns false, writing nothing, when the carrier holds something else by
    /// now. Optional: without it the host reads, compares and then calls <see cref="WriteAsync"/>.
    /// </summary>
    public Func<byte[], string?, CancellationToken, Task<bool>>? WriteIfUnchangedAsync { get; init; }
}
