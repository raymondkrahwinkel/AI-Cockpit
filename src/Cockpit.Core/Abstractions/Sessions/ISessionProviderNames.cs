namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// The names the operator sees for the session providers plugins registered (AC-1449).
/// </summary>
public interface ISessionProviderNames
{
    /// <summary>
    /// The display name of the provider registered as <paramref name="providerId"/>; null when none is registered.
    /// </summary>
    string? DisplayNameOf(string providerId);
}
