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
public sealed record ProjectMemorySourceRegistration(string Scheme, string Title, string Instruction);
