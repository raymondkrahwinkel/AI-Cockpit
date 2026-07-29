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
    /// <summary>
    /// The longest <see cref="Detail"/> this type ever hands back (AC-503 adversarial review, Opus confirming
    /// round). <see cref="Confirmed"/> is fed a plugin's own value, which in turn is often a tool's raw text
    /// response — server-controlled, not operator-typed, and unbounded until this constant: a multi-kilobyte
    /// document listing would otherwise reach the project editor's row exactly as it came off the wire. 200 is
    /// comfortably longer than the confirmation sentences this type's own doc comment illustrates ("24 documents,
    /// last changed 2 hours ago") while still reading as the "short" string that doc comment promises.
    /// </summary>
    public const int MaxDetailLength = 200;

    public static ProjectMemorySourceReachabilityResult NotSignedIn { get; } = new(ProjectMemorySourceReachability.NotSignedIn);

    public static ProjectMemorySourceReachabilityResult NotFound { get; } = new(ProjectMemorySourceReachability.NotFound);

    /// <summary>
    /// <paramref name="detail"/> collapsed onto a single line and clamped to <see cref="MaxDetailLength"/> — never
    /// the raw value verbatim, since a plugin's <c>detail</c> is only ever as trustworthy as whatever it read it
    /// from (AC-503 adversarial review, Opus confirming round: nothing enforced the "short confirmation string" this
    /// type's own doc comment promises until this clamp existed). A null or blank value passes through unchanged —
    /// there is nothing to shorten, and the row falls back to its own fixed confirmation sentence.
    /// </summary>
    public static ProjectMemorySourceReachabilityResult Confirmed(string? detail = null) =>
        new(ProjectMemorySourceReachability.Confirmed, _Clamp(detail));

    private static string? _Clamp(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return detail;
        }

        var singleLine = string.Join(' ', detail.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return singleLine.Length > MaxDetailLength
            ? string.Concat(singleLine.AsSpan(0, MaxDetailLength - 1), "…")
            : singleLine;
    }
}
