using Cockpit.Core.Profiles;

namespace Cockpit.App.Services;

/// <summary>
/// A profile store that writes each profile under the label it was read with (AC-1585), so a rename keeps what only
/// the store holds, such as a secret it never handed out.
/// </summary>
public interface IProfileEditStore
{
    /// <summary>
    /// Saves <paramref name="profiles"/>, each paired with the label it was read under, or null for one added since.
    /// A profile read but no longer in the list is removed.
    /// </summary>
    Task SaveEditsAsync(IReadOnlyList<(string? OriginalLabel, SessionProfile Profile)> profiles, CancellationToken cancellationToken = default);
}
