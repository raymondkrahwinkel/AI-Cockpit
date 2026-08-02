using System.Collections.ObjectModel;

namespace Cockpit.Core.Projects;

/// <summary>
/// What a session works on (AC-158): the source folder, which MCP servers are on, the profile it starts under
/// and whether its sessions isolate in a worktree. A session is profile × project — the profile stays who and
/// how you work (provider, model, credentials), the project says what you work on. Without it, working on a
/// second codebase meant a second near-identical profile.
/// <para>
/// A project <em>uses</em> a profile, it never extends one: it names one by label and overrides or supplements
/// what that profile defaults to (<see cref="Cockpit.Core.Sessions.SessionStartDefaults"/> is the only place the
/// two meet). A profile knows nothing about projects and keeps working without one.
/// </para>
/// </summary>
/// <param name="Id">Stable id, referenced by a session and never shown.</param>
/// <param name="Name">The project's display name — renamable, and free to collide with another project's name.</param>
public sealed record Project(string Id, string Name)
{
    /// <summary>Free-text note on what this project is, shown under its name in the launcher and the manager.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// The folder its sessions start in. Null/blank for a project with no source of its own — an administrative
    /// project is a perfectly good project, and this model is not only for repositories.
    /// </summary>
    public string? SourceDirectory { get; init; }

    /// <summary>The Git URL <see cref="SourceDirectory"/> was cloned from (AC-90), so the manager can show where it came from. Null when the folder was picked rather than cloned.</summary>
    public string? GitUrl { get; init; }

    /// <summary>
    /// The profile its sessions start under, matched by label the way <c>NewSessionPrefill.ProfileLabel</c> is —
    /// deliberately a label and not a profile: a project points at a profile, it does not own or extend one. A
    /// label matching no profile leaves the dialog on its own default rather than failing the start.
    /// </summary>
    public string? DefaultProfileLabel { get; init; }

    /// <summary>
    /// How the profile should behave here, appended to the session's system prompt (the AC-180 seam) rather than
    /// replacing anything the profile says. This is the override idea at its plainest: the same profile works
    /// differently per project without a second profile existing. Null/blank appends nothing.
    /// </summary>
    public string? BehaviorPrompt { get; init; }

    /// <summary>
    /// Whether new sessions here isolate in their own git worktree (AC-85) when <see cref="SourceDirectory"/> is
    /// a repository. A default only: worktree stays a per-session choice, still overridable in the dialog.
    /// </summary>
    public bool IsolateInWorktreeByDefault { get; init; }

    /// <summary>Which MCP servers its sessions see, as a change on top of the global registry rather than a list of its own — see <see cref="ProjectMcpOverlay"/>.</summary>
    public ProjectMcpOverlay McpOverlay { get; init; } = ProjectMcpOverlay.None;

    /// <summary>
    /// Whatever else a project's sessions may need to read, follow or look things up in (AC-483): a memory folder
    /// and a Depot project together, tomorrow an instruction file — see <see cref="ProjectResource"/> and
    /// <see cref="ProjectResourceRole"/>. In the idiom of <see cref="AdditionalInfo"/>: a plain list, in the order
    /// they were added, empty for the (still common) project that keeps none.
    /// </summary>
    public IReadOnlyList<ProjectResource> Resources { get; init; } = [];

    /// <summary>
    /// Where this project's memory lives — a folder, deliberately separate from <see cref="SourceDirectory"/>,
    /// because what a project knows and what it is made of are not the same place and often not the same disk.
    /// Told to the session as part of its standing instructions, so it can go and look rather than be told again
    /// every time.
    /// <para>
    /// Free text rather than a path type: a plugin will contribute other kinds of reference (a Depot project,
    /// AC-165/166), and those are not folders. The host stores what it is given and says it plainly.
    /// </para>
    /// <para>
    /// Mirrors the first <see cref="ProjectResourceRole.Memory"/> row in <see cref="Resources"/> rather than
    /// holding a value of its own (AC-483: a project can now carry more than one memory source, and this field
    /// predates that by a long way). Kept only so nothing that already reads or writes it — <c>SessionStartDefaults</c>,
    /// the project editor, every test that does <c>project with { MemoryRef = "..." }</c> — has to change: reading
    /// always answers from <see cref="Resources"/>, and writing (an <c>init</c> accessor, because that is the only
    /// kind of setter a record's <c>with</c> expression and object initializers can call) folds the value into that
    /// same first row rather than keeping a second, independent place for it to disagree with.
    /// </para>
    /// <para>
    /// ⚠️ Because both names write the same place, an initializer that sets <em>both</em> is order-dependent: the
    /// later one wins, so <c>with { MemoryRef = "a", Resources = [...] }</c> and the same two lines swapped produce
    /// different projects. Nothing does that today (checked), and nothing should: set one or the other. The way out
    /// is not a cleverer setter — no accessor can make two writes to one place commute — but for the callers that
    /// still say <c>MemoryRef</c> to say <see cref="Resources"/> instead, after which this member goes. AC-485 is
    /// where that starts, since the project editor is the last writer of consequence.
    /// </para>
    /// </summary>
    public string? MemoryRef
    {
        get => Resources.FirstOrDefault(resource => resource.Role == ProjectResourceRole.Memory)?.Reference;
        init => Resources = _WithMemoryReference(Resources, value);
    }

    /// <summary>
    /// <paramref name="resources"/> with its first <see cref="ProjectResourceRole.Memory"/> row's
    /// <see cref="ProjectResource.Reference"/> set to <paramref name="reference"/>: replaced in place if such a row
    /// exists, appended if it does not.
    /// <para>
    /// A null or blank <paramref name="reference"/> removes <em>every</em> Memory row, not just the first: AC2 lets
    /// a project keep more than one (a local folder and a Depot project together), and <see cref="MemoryRef"/> is a
    /// singular name for "the memory this project keeps" — if clearing it left a second Memory row standing, the
    /// getter would go on reporting memory that is, from this call's point of view, supposed to be gone. Removing
    /// only the first would make <c>with { MemoryRef = null }</c> lie about what it just did.
    /// </para>
    /// </summary>
    private static IReadOnlyList<ProjectResource> _WithMemoryReference(IReadOnlyList<ProjectResource> resources, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return [.. resources.Where(resource => resource.Role != ProjectResourceRole.Memory)];
        }

        var index = -1;
        for (var i = 0; i < resources.Count; i++)
        {
            if (resources[i].Role == ProjectResourceRole.Memory)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return [.. resources, new ProjectResource(reference, ProjectResourceRole.Memory)];
        }

        var updated = new List<ProjectResource>(resources) { [index] = resources[index] with { Reference = reference } };
        return updated;
    }

    /// <summary>
    /// The project's logo: the path of the image the cockpit copied into its own storage when the operator picked a
    /// file or gave a URL. A copy rather than the original's path (AC-162), so the card keeps its picture when the
    /// source moves, is renamed, or lives on a drive that is not plugged in. Null for a project without one, which
    /// shows its initial instead.
    /// </summary>
    public string? LogoPath { get; init; }

    /// <summary>
    /// When a session was last started on this project, or null for one never opened. Written by the host at
    /// launch, so the overview can lead with what the operator actually works on rather than the order the
    /// projects happen to be stored in.
    /// </summary>
    public DateTimeOffset? LastOpenedAt { get; init; }

    /// <summary>
    /// Whatever else belongs with this project, under labels the operator chose (AC-295): the repository it lives in,
    /// the customer's website, a contact. Deliberately not a field per kind of information — the cockpit cannot know
    /// which kinds a project needs, and each new one would otherwise cost a model change.
    /// <para>
    /// Empty for most projects. Shown where a project is read rather than where it is started, and a value that is an
    /// <c>http(s)</c> URL is shown as a link (see <see cref="ProjectInfoField.IsWebLink"/>).
    /// </para>
    /// </summary>
    public IReadOnlyList<ProjectInfoField> AdditionalInfo { get; init; } = [];

    /// <summary>Whether this project keeps any information of its own, so a surface leaves the block out rather than holding an empty space open.</summary>
    public bool HasAdditionalInfo => AdditionalInfo.Count > 0;

    /// <summary>
    /// Which category this project sits under in the manager's list (AC-618) — "Privé", "Werk", whatever the
    /// operator types; null/blank groups it under "Uncategorized" instead. Always local, even for a project bound
    /// to a shared Depot definition: the operator who shares a project does not get to impose their own filing on
    /// everyone who opens it, the same local/portable line <see cref="ProjectResource"/> already draws for memory.
    /// <para>
    /// Compared case-insensitively (<see cref="StringComparison.OrdinalIgnoreCase"/> — never the culture-sensitive
    /// default, which is exactly the AC-372 class of bug: a Turkish locale's lowercase of <c>I</c> is not <c>i</c>,
    /// so <c>"Werk"</c> and <c>"werk"</c> would stop matching there). This project's own text is kept exactly as
    /// typed rather than rewritten to a shared casing — the group heading it shows under is what carries the
    /// "shown as first typed" rule; see <see cref="ProjectSettings.CategoryOrder"/>.
    /// </para>
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// What this project is called elsewhere (AC-317), under the key the plugin that asked registered: the YouTrack
    /// project it is tracked in, the repository it lives in. Where <see cref="AdditionalInfo"/> is what the operator
    /// wants to remember, this is what a plugin resolves — a value it queries with, not a note anyone reads.
    /// <para>
    /// Held by the host rather than by each plugin because three plugins ask the same question about one project, and
    /// because a link must survive its plugin being uninstalled: a value under a key nothing claims is carried through
    /// untouched, so reinstalling the plugin finds the project still linked.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string> PluginFields { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>What this project is called under <paramref name="key"/>, or null when nothing linked it there. Keys match exactly, the way plugin ids and intent actions do.</summary>
    public string? LinkedAs(string key) =>
        PluginFields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>A new project with a generated id, mirroring <c>Workspace.Create</c>.</summary>
    public static Project Create(string name) => new(Guid.NewGuid().ToString("n"), name);
}
