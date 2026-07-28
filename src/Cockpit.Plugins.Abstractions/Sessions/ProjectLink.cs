namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// How a plugin names a cockpit project without knowing what one is (#AC-419): not by id, but by what the project is
/// linked as elsewhere — the YouTrack project it is tracked in, the repository it lives in. The host finds the project
/// carrying <paramref name="Value"/> under <paramref name="FieldKey"/>.
/// <para>
/// The same link <see cref="ICockpitHost.AddProjectField"/> stores, read the other way round:
/// <see cref="ICockpitHost.GetProjectFieldValueAsync"/> starts from a session and asks what its project is called over
/// there, while this starts from that name and asks which project answers to it. Deliberately a key and a value rather
/// than a project — a plugin already holds both (it registered the key, and the issue it is looking at names the
/// value), and neither tells it anything about the host's project model.
/// </para>
/// </summary>
/// <param name="FieldKey">
/// The project field the link is stored under — the key registered with <see cref="ICockpitHost.AddProjectField"/>
/// (<c>youtrack.project</c>, <c>github.repository</c>). Matched exactly, the way plugin ids and intent actions are.
/// </param>
/// <param name="Value">
/// What the project is linked as under that key — what <see cref="ICockpitHost.GetProjectFieldValueAsync"/> would hand
/// back for it (<c>AC</c>, <c>owner/repo</c>). Matched case-insensitively: a tracker's short name and a repository's
/// owner/name are not case-sensitive identifiers.
/// </param>
public sealed record ProjectLink(string FieldKey, string Value);
