namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// A place a plugin says a project's memory can live, other than a folder (AC-165/166) — a Depot project, say.
/// The plugin describes the scheme it owns and how to act on it; the host turns a project's <c>MemoryRef</c>
/// (<c>&lt;scheme&gt;:&lt;value&gt;</c>) into a sentence the session can follow.
/// </summary>
/// <remarks>
/// Declared rather than resolved eagerly: the host only ever reads <see cref="Title"/> and
/// <see cref="Instruction"/> back into a prompt, so nothing here needs a network call at registration time.
/// </remarks>
/// <param name="Scheme">
/// The prefix a project's <c>MemoryRef</c> carries this source under — <c>depot</c> in <c>depot:cockpit</c>. Matched
/// case-insensitively against a project's stored reference. Prefix it with your plugin's own vocabulary where a
/// clash is plausible; the first plugin to register a scheme keeps it, the same agreement
/// <see cref="ProjectFieldRegistration.Key"/> makes for a project field.
/// </param>
/// <param name="Title">
/// How the source is named back to the operator and the session — "Depot project".
/// </param>
/// <param name="Instruction">
/// The sentence appended after the session is told where its memory lives, saying how to actually reach it —
/// "Read it through the Depot MCP's <c>read</c> tool." Told rather than loaded for the same reason a folder
/// reference is only ever named, not opened: the host does not run your MCP tools on the session's behalf.
/// </param>
public sealed record ProjectMemorySourceRegistration(string Scheme, string Title, string Instruction)
{
    // AC-502/AC-503/AC-499: trailing optional members rather than widening the primary constructor — a plugin
    // prebuilt against an older assembly still calls the original 3-parameter ctor by its exact IL signature,
    // the same binary-compat reasoning McpServerContribution's own remark gives.

    /// <summary>
    /// Optionally lists the locations this source can point at (AC-502), so the project editor's
    /// <c>Choose…</c> button can offer a picker of names instead of staying disabled.
    /// </summary>
    /// <remarks>
    /// Null means "cannot enumerate" — the reference box stays free-typed only. Never called eagerly at
    /// registration time. Distinguishes "here are the locations" from "not signed in yet" from "the call failed"
    /// via <see cref="ProjectMemorySourceLocationsResult.Outcome"/>.
    /// </remarks>
    public Func<CancellationToken, Task<ProjectMemorySourceLocationsResult>>? ListLocationsAsync { get; init; }

    /// <summary>
    /// Drives this source's own sign-in when <see cref="ListLocationsAsync"/> answers
    /// <see cref="ProjectMemorySourceLocationsOutcome.AuthorizationRequired"/> (AC-502), returning whether it
    /// produced a usable standing to list from.
    /// </summary>
    /// <remarks>
    /// Null when <see cref="ListLocationsAsync"/> is null, or when a source that can list never needs a sign-in.
    /// </remarks>
    public Func<CancellationToken, Task<bool>>? SignInAsync { get; init; }

    /// <summary>
    /// Confirms whether a value the operator typed for this source actually resolves to something (AC-503).
    /// Takes the row's typed value (the bare identifier) and a cancellation token the project editor cancels the
    /// instant a newer edit supersedes this one.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> (the default) means "no check available" — nothing shown under the row.
    /// </remarks>
    public Func<string, CancellationToken, Task<ProjectMemorySourceReachabilityResult>>? CheckReachability { get; init; }

    /// <summary>
    /// Which <see cref="ProjectMemorySourceFamily.Key"/> this instance belongs to (AC-499), grouping however many
    /// connections a plugin has configured under one entry in the picker instead of one row per connection.
    /// </summary>
    /// <remarks>
    /// Matched case-insensitively against a declared family's own key. Null (the default) is today's behaviour —
    /// this registration gets its own row in the picker.
    /// </remarks>
    public string? FamilyKey { get; init; }

    /// <summary>
    /// How this instance is named in its family's own instance dropdown (AC-499) — "Depot (krahwinkel-it)" beside
    /// a sibling "Depot (synvolution)" — rather than the bare <see cref="Title"/> repeated.
    /// </summary>
    /// <remarks>
    /// Blank or null falls back to <see cref="Title"/>.
    /// </remarks>
    public string? InstanceTitle { get; init; }

    /// <summary>
    /// Compares <see cref="Scheme"/>, <see cref="Title"/>, <see cref="Instruction"/>, <see cref="FamilyKey"/> and
    /// <see cref="InstanceTitle"/> — never the delegate members — deliberately overriding the record-generated
    /// equality that would otherwise include them.
    /// </summary>
    /// <remarks>
    /// Two delegates freshly built for the same connection are never reference-equal even when they close over
    /// identical data, which would make an unrelated save look "changed" to a settings view's before/after diff.
    /// <see cref="Scheme"/> and <see cref="FamilyKey"/> compare <see cref="StringComparison.OrdinalIgnoreCase"/>
    /// to agree with how a project's stored <c>MemoryRef</c> resolves. A <c>with</c> expression that only
    /// replaces a delegate produces a registration equal to the original — worth remembering before a
    /// <c>Distinct</c>/<c>HashSet</c>/dictionary-key use of these registrations.
    /// </remarks>
    public bool Equals(ProjectMemorySourceRegistration? other) =>
        other is not null
        && string.Equals(Scheme, other.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Title, other.Title, StringComparison.Ordinal)
        && string.Equals(Instruction, other.Instruction, StringComparison.Ordinal)
        && string.Equals(FamilyKey, other.FamilyKey, StringComparison.OrdinalIgnoreCase)
        && string.Equals(InstanceTitle, other.InstanceTitle, StringComparison.Ordinal);

    public override int GetHashCode() =>
        HashCode.Combine(Scheme.ToUpperInvariant(), Title, Instruction, FamilyKey?.ToUpperInvariant(), InstanceTitle);
}
