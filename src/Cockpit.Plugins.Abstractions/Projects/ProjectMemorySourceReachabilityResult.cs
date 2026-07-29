namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>The answer to <see cref="ProjectMemorySourceRegistration.CheckReachability"/> (AC-503): a named state plus, only when useful, a short confirmation string to show under the row.</summary>
/// <param name="State">What the check found.</param>
/// <param name="Detail">
/// Shown under the row on <see cref="ProjectMemorySourceReachability.Confirmed"/> in place of a fixed sentence —
/// "24 documents, last changed 2 hours ago", say. Null falls back to a fixed confirmation sentence. Ignored for any
/// other <see cref="State"/>: <see cref="ProjectMemorySourceReachability.NotSignedIn"/> and
/// <see cref="ProjectMemorySourceReachability.NotFound"/> each show their own fixed sentence, the same as the
/// existing broken-reference hint does — nothing plugin-supplied is shown for those, so a plugin cannot accidentally
/// leak connection detail into a state meant to read as a plain, honest "no".
/// </param>
public sealed record ProjectMemorySourceReachabilityResult(ProjectMemorySourceReachability State, string? Detail = null)
{
    public static ProjectMemorySourceReachabilityResult NotSignedIn { get; } = new(ProjectMemorySourceReachability.NotSignedIn);

    public static ProjectMemorySourceReachabilityResult NotFound { get; } = new(ProjectMemorySourceReachability.NotFound);

    public static ProjectMemorySourceReachabilityResult Confirmed(string? detail = null) => new(ProjectMemorySourceReachability.Confirmed, detail);
}
