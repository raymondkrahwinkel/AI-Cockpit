namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// A place a plugin says a project's memory can live, other than a folder (AC-165/166) — a Depot project, say.
/// <see cref="Project.MemoryRef"/> already allowed free text for exactly this reason; what was missing was a way for
/// the session to be told <em>how</em> to reach a reference it does not itself understand. This registration is
/// that missing half: the plugin describes the scheme it owns and how to act on it, and the host turns a project's
/// <c>MemoryRef</c> of the shape <c>&lt;scheme&gt;:&lt;value&gt;</c> into a sentence the session can follow, the same
/// way it already does for a folder.
/// <para>
/// Declared rather than resolved eagerly, the way <see cref="ProjectFieldRegistration"/> is: the host only ever
/// reads <see cref="Title"/> and <see cref="Instruction"/> back into a prompt, so there is nothing here that needs
/// a network call or credentials at registration time.
/// </para>
/// </summary>
/// <param name="Scheme">
/// The prefix a project's <c>MemoryRef</c> carries this source under — <c>depot</c> in <c>depot:cockpit</c>. Matched
/// case-insensitively against a project's stored reference. Prefix it with your plugin's own vocabulary where a
/// clash is plausible; the first plugin to register a scheme keeps it, the same agreement
/// <see cref="ProjectFieldRegistration.Key"/> makes for a project field.
/// </param>
/// <param name="Title">How the source is named back to the operator and the session — "Depot project".</param>
/// <param name="Instruction">
/// The sentence appended after the session is told where its memory lives, saying how to actually reach it —
/// "Read it through the Depot MCP's <c>read</c> tool." Told rather than loaded for the same reason a folder
/// reference is only ever named, not opened: the host does not run your MCP tools on the session's behalf.
/// </param>
public sealed record ProjectMemorySourceRegistration(string Scheme, string Title, string Instruction)
{
    // AC-502: two trailing optional members rather than widening the primary constructor further — a plugin
    // prebuilt against an older Cockpit.Plugins.Abstractions.dll still calls this record's original 3-parameter
    // constructor by its exact IL signature, the same binary-compat reasoning McpServerContribution's own remark
    // gives for its init-only properties.

    /// <summary>
    /// Optionally lists the locations this source can point at (AC-502) — a Depot connection's own projects, say —
    /// so the project editor's <c>Choose…</c> button can offer a picker of names instead of staying disabled. Null
    /// means "cannot enumerate": <c>Choose…</c> stays exactly as disabled as it was before this member existed, and
    /// the reference box stays free-typed only. Never called eagerly at registration time — only when the operator
    /// actually opens the picker — since listing is a network call and registering a source must not be.
    /// <para>
    /// Distinguishes "here are the locations" from "not signed in yet" from "the call failed" via
    /// <see cref="ProjectMemorySourceLocationsResult.Outcome"/>, so the dialog never shows an empty list for a
    /// reason other than "there really is nothing here" (AC-502 criteria 4 and 5).
    /// </para>
    /// </summary>
    public Func<CancellationToken, Task<ProjectMemorySourceLocationsResult>>? ListLocationsAsync { get; init; }

    /// <summary>
    /// Drives this source's own sign-in when <see cref="ListLocationsAsync"/> answers
    /// <see cref="ProjectMemorySourceLocationsOutcome.AuthorizationRequired"/> (AC-502 criterion 4), returning
    /// whether it produced a usable standing to list from. Null when <see cref="ListLocationsAsync"/> is null, or
    /// when a source that can list never needs a sign-in of its own.
    /// </summary>
    public Func<CancellationToken, Task<bool>>? SignInAsync { get; init; }

    /// <summary>
    /// Compares only <see cref="Scheme"/>, <see cref="Title"/> and <see cref="Instruction"/> — never
    /// <see cref="ListLocationsAsync"/>/<see cref="SignInAsync"/> — deliberately overriding the record-generated
    /// equality that would otherwise include them. Two delegates freshly built for the very same connection
    /// (<c>DepotMemorySource.BuildRegistrationPairs</c>, say) are never reference-equal to each other even when
    /// they close over identical data, which would make every one of that connection's own unrelated saves look
    /// "changed" to <c>DepotSettingsControl._SyncMemorySources</c>'s before/after diff and force an unnecessary
    /// Remove+Add of a scheme that did not actually change — the very thing that diff exists to skip. Content
    /// identity here means the three fields a session's own standing instructions are actually built from.
    /// <para>
    /// <see cref="Scheme"/> compares <see cref="StringComparison.OrdinalIgnoreCase"/> to agree with
    /// <see cref="ProjectMemorySourceRegistry"/> and a project's stored <c>MemoryRef</c>, both of which resolve a
    /// scheme case-insensitively (see <see cref="Scheme"/>'s own doc comment) — comparing it case-sensitively here
    /// would let a pure-case rename of the same scheme read as "changed" to <c>_SyncMemorySources</c> while every
    /// other consumer still treats it as the one source it always was.
    /// </para>
    /// <para>
    /// Because equality ignores the delegates, a <c>with</c> expression that only replaces
    /// <see cref="ListLocationsAsync"/>/<see cref="SignInAsync"/> produces a registration equal to the original —
    /// harmless today (nothing in this codebase builds one that way), but a future <c>Distinct</c>/<c>HashSet</c>/
    /// dictionary-key use of these registrations would silently keep whichever instance it saw first, delegates and
    /// all. Worth remembering before reaching for one of those.
    /// </para>
    /// </summary>
    public bool Equals(ProjectMemorySourceRegistration? other) =>
        other is not null
        && string.Equals(Scheme, other.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Title, other.Title, StringComparison.Ordinal)
        && string.Equals(Instruction, other.Instruction, StringComparison.Ordinal);

    public override int GetHashCode() =>
        HashCode.Combine(Scheme.ToUpperInvariant(), Title, Instruction);
}
