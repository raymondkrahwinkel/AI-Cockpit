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
    /// Confirms whether a value the operator typed for this source actually resolves to something (AC-503) — the
    /// plugin-resource half of the confirmation a <c>Reference</c> row already gets for a broken absolute path
    /// (AC-485). Takes the row's typed value (the bare identifier, not <c>"{Scheme}:{value}"</c>) and a cancellation
    /// token the project editor cancels the instant a newer edit supersedes this one, the same version-guard its own
    /// <c>Reference</c>-probe diagnostics already use.
    /// <para>
    /// <see langword="null"/> (the default) means "no check available" — a row whose source did not set this
    /// behaves exactly as it always has: nothing shown under the row, the same as a plugin that predates AC-503, or
    /// one whose author decided the value cannot be cheaply confirmed at all.
    /// </para>
    /// </summary>
    public Func<string, CancellationToken, Task<ProjectMemorySourceReachabilityResult>>? CheckReachability { get; init; }

    /// <summary>
    /// Which <see cref="ProjectMemorySourceFamily.Key"/> this instance belongs to (AC-499) — "depot", say, grouping
    /// however many connections a plugin has configured under one "Depot" entry in the picker instead of one row
    /// per connection. Matched case-insensitively against a declared family's own <see cref="ProjectMemorySourceFamily.Key"/>,
    /// the same agreement <see cref="Scheme"/> makes with a project's stored reference.
    /// <para>
    /// Null (the default) is exactly today's behaviour: this registration gets its own row in the picker, the way
    /// every registration did before AC-499 existed. A <see cref="ProjectMemorySourceFamily"/> costs a plugin
    /// nothing to skip — a scheme that names one no <c>AddProjectMemorySourceFamily</c> call ever declared is simply
    /// never grouped, and falls back to its own row the same as null would.
    /// </para>
    /// </summary>
    public string? FamilyKey { get; init; }

    /// <summary>
    /// How this instance is named in its family's own instance dropdown (AC-499) — "Depot (krahwinkel-it)" beside a
    /// sibling "Depot (synvolution)" — rather than the bare <see cref="Title"/> every registration under the same
    /// family would otherwise repeat. Blank or null falls back to <see cref="Title"/>, the same "leave it alone
    /// rather than guess" default the rest of this record follows.
    /// </summary>
    public string? InstanceTitle { get; init; }

    /// <summary>
    /// Compares <see cref="Scheme"/>, <see cref="Title"/>, <see cref="Instruction"/>, <see cref="FamilyKey"/> and
    /// <see cref="InstanceTitle"/> — never <see cref="ListLocationsAsync"/>/<see cref="SignInAsync"/>/
    /// <see cref="CheckReachability"/> — deliberately overriding the record-generated equality that would otherwise
    /// include them. Two delegates freshly built for the very same connection (<c>DepotMemorySource.BuildRegistrationPairs</c>,
    /// say) are never reference-equal to each other even when they close over identical data, which would make every
    /// one of that connection's own unrelated saves look "changed" to <c>DepotSettingsControl._SyncMemorySources</c>'s
    /// before/after diff and force an unnecessary Remove+Add of a scheme that did not actually change — the very
    /// thing that diff exists to skip. Content identity here means the fields a session's own standing instructions,
    /// and the picker's own labels, are actually built from.
    /// <para>
    /// <see cref="Scheme"/> and <see cref="FamilyKey"/> both compare <see cref="StringComparison.OrdinalIgnoreCase"/>
    /// to agree with <see cref="ProjectMemorySourceRegistry"/>, a project's stored <c>MemoryRef</c>, and
    /// <see cref="ProjectMemorySourceFamily.Key"/> matching, all of which resolve their own key case-insensitively —
    /// comparing either case-sensitively here would let a pure-case rename read as "changed" to
    /// <c>_SyncMemorySources</c> while every other consumer still treats it as the one source or family it always
    /// was.
    /// </para>
    /// <para>
    /// AC-499: <see cref="FamilyKey"/> and <see cref="InstanceTitle"/> are included deliberately, unlike the three
    /// delegates above — both are content the operator actually sees change, not incidental wiring. Moving an
    /// instance to a different family (or out of one) changes which row of the picker it appears under, and
    /// renaming an instance (a Depot connection the operator retitled) changes what its own row in the instance
    /// dropdown reads — either is exactly the kind of visible change <c>_SyncMemorySources</c>'s diff exists to
    /// catch, the same reason a title or instruction change already does.
    /// </para>
    /// <para>
    /// Because equality ignores the delegates, a <c>with</c> expression that only replaces one of them produces a
    /// registration equal to the original — harmless today (nothing in this codebase builds one that way), but a
    /// future <c>Distinct</c>/<c>HashSet</c>/dictionary-key use of these registrations would silently keep whichever
    /// instance it saw first, delegates and all. Worth remembering before reaching for one of those.
    /// </para>
    /// </summary>
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
