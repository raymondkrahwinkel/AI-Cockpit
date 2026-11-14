using Cockpit.Core.Profiles;

namespace Cockpit.App.Services;

/// <summary>
/// A profile store that writes each profile under the label it was read with (AC-1585), so a rename keeps what only
/// the store holds, such as a secret it never handed out.
/// </summary>
public interface IProfileEditStore
{
    /// <summary>
    /// Saves <paramref name="profiles"/> with their original labels and optional sources for copied secrets.
    /// A profile read but no longer in the list is removed.
    /// </summary>
    Task SaveEditsAsync(IReadOnlyList<(string? OriginalLabel, SessionProfile Profile, string? CopyFromLabel)> profiles, CancellationToken cancellationToken = default);
}
