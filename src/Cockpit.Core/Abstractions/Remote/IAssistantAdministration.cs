using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Remote;

/// <summary>
/// A server's own assistant over its admin API (AC-1475): whether it runs and the profile it runs on. No answer carries
/// a credential, and the consent bypass only reads; nothing here can widen which consent cards the assistant may skip.
/// </summary>
public interface IAssistantAdministration
{
    /// <summary>
    /// The switch, the server's availability and its reason, the profile without its secrets, and the bypass summary.
    /// </summary>
    Task<RemoteAssistantSettings> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns the assistant on or off, changing nothing else the server keeps beside the switch.
    /// </summary>
    Task<RemoteAssistantSettings> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the slot's whole profile as the shared Assistant Profile editor holds it (AC-1995) and restarts the assistant;
    /// each secret that reads as the server's mask stays as the server holds it. Throws <see cref="ArgumentException"/>
    /// with the server's reason when it refuses, a new credential included.
    /// </summary>
    Task<RemoteAssistantSettings> SaveProfileAsync(SessionProfile profile, bool replacesStandingInstruction, CancellationToken cancellationToken = default);

    /// <summary>
    /// Has the server copy its own profile <paramref name="label"/> into the slot, secrets included; null when it has
    /// no profile by that label.
    /// </summary>
    Task<RemoteAssistantSettings?> CopyProfileFromAsync(string label, CancellationToken cancellationToken = default);
}
