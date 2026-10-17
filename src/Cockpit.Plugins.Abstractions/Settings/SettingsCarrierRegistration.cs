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
    Func<byte[], CancellationToken, Task> WriteAsync);
