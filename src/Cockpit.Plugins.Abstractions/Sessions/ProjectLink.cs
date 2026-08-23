namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// How a plugin names a cockpit project without knowing what one is (#AC-419): not by id, but by what the project is
/// linked as elsewhere — the YouTrack project it is tracked in, the repository it lives in. The host finds the
/// project carrying <paramref name="Value"/> under <paramref name="FieldKey"/>.
/// </summary>
/// <remarks>
/// The same link <see cref="ICockpitHost.AddProjectField"/> stores, read the other way round:
/// <see cref="ICockpitHost.GetProjectFieldValueAsync"/> starts from a session and asks what its project is called
/// over there, while this starts from that name and asks which project answers to it.
/// </remarks>
/// <param name="FieldKey">
/// The project field the link is stored under — the key registered with <see cref="ICockpitHost.AddProjectField"/>
/// (<c>youtrack.project</c>, <c>github.repository</c>). Matched exactly, the way plugin ids and intent actions are.
/// </param>
/// <param name="Value">
/// What the project is linked as under that key — what <see cref="ICockpitHost.GetProjectFieldValueAsync"/> would hand
/// back for it (<c>AC</c>, <c>owner/repo</c>). Matched case-insensitively: a tracker's short name and a repository's
/// owner/name are not case-sensitive identifiers. A project whose stored value under <paramref name="FieldKey"/> names
/// several identifiers (AC-884, e.g. a YouTrack field naming <c>EWB, AT, EJ</c>) matches on any one of them — that
/// matching happens host-side, this record itself still carries only the single value the plugin is asking about.
/// </param>
public sealed record ProjectLink(string FieldKey, string Value);
