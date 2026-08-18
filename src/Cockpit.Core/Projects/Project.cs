using System.Collections.ObjectModel;
using System.Text;

namespace Cockpit.Core.Projects;

// What a session works on (AC-158): the source folder, which MCP servers are on, the profile it starts under
// and whether its sessions isolate in a worktree. A session is profile × project — the profile stays who and
// how you work (provider, model, credentials), the project says what you work on. Without it, working on a
// second codebase meant a second near-identical profile.
//
// A project *uses* a profile, it never extends one: it names one by label and overrides or supplements
// what that profile defaults to (`Cockpit.Core.Sessions.SessionStartDefaults` is the only place the
// two meet). A profile knows nothing about projects and keeps working without one.
//
// `Id`: Stable id, referenced by a session and never shown.
// `Name`: The project's display name — renamable, and free to collide with another project's name.
public sealed record Project(string Id, string Name)
{
    // Free-text note on what this project is, shown under its name in the launcher and the manager.
    public string? Description { get; init; }

    // The repositories a project's sessions can start in, in the order the operator added them. Item 0 is what
    // SourceDirectory below has always been; anything after it covers a Waymark-shaped project (a web repo and an
    // android repo, neither nested in the other). Empty for a project with no source of its own.
    public IReadOnlyList<ProjectRepository> SourceDirectories { get; init; } = [];

    // The folder its sessions start in by default — item 0 of SourceDirectories, read-only, so every existing
    // reader keeps working unchanged for a single-repository project. A session on a different declared repository
    // picks it explicitly; this property never claims to know which one that will be.
    public string? SourceDirectory => SourceDirectories.Count > 0 ? SourceDirectories[0].Path : null;

    // The Git URL `SourceDirectory` was cloned from (AC-90), so the manager can show where it came from. Null when the folder was picked rather than cloned.
    public string? GitUrl { get; init; }

    // The profile its sessions start under, matched by label the way `NewSessionPrefill.ProfileLabel` is —
    // deliberately a label and not a profile: a project points at a profile, it does not own or extend one. A
    // label matching no profile leaves the dialog on its own default rather than failing the start.
    public string? DefaultProfileLabel { get; init; }

    // How the profile should behave here, appended to the session's system prompt (the AC-180 seam) rather than
    // replacing anything the profile says. This is the override idea at its plainest: the same profile works
    // differently per project without a second profile existing. Null/blank appends nothing.
    public string? BehaviorPrompt { get; init; }

    // Whether new sessions here isolate in their own git worktree (AC-85) when `SourceDirectory` is
    // a repository. A default only: worktree stays a per-session choice, still overridable in the dialog.
    public bool IsolateInWorktreeByDefault { get; init; }

    // Which MCP servers its sessions see, as a change on top of the global registry rather than a list of its own — see `ProjectMcpOverlay`.
    public ProjectMcpOverlay McpOverlay { get; init; } = ProjectMcpOverlay.None;

    // Whatever else a project's sessions may need to read, follow or look things up in (AC-483): a memory folder
    // and a Depot project together, tomorrow an instruction file — see `ProjectResource` and
    // `ProjectResourceRole`. In the idiom of `AdditionalInfo`: a plain list, in the order
    // they were added, empty for the (still common) project that keeps none.
    public IReadOnlyList<ProjectResource> Resources { get; init; } = [];

    // Where this project's memory lives — a folder, deliberately separate from `SourceDirectory`,
    // because what a project knows and what it is made of are not the same place and often not the same disk.
    // Told to the session as part of its standing instructions, so it can go and look rather than be told again
    // every time.
    //
    // Free text rather than a path type: a plugin will contribute other kinds of reference (a Depot project,
    // AC-165/166), and those are not folders. The host stores what it is given and says it plainly.
    //
    // Mirrors the first `ProjectResourceRole.Memory` row in `Resources` rather than
    // holding a value of its own (AC-483: a project can now carry more than one memory source, and this field
    // predates that by a long way). Kept only so nothing that already reads or writes it — `SessionStartDefaults`,
    // the project editor, every test that does `project with { MemoryRef = "..." }` — has to change: reading
    // always answers from `Resources`, and writing (an `init` accessor, because that is the only
    // kind of setter a record's `with` expression and object initializers can call) folds the value into that
    // same first row rather than keeping a second, independent place for it to disagree with.
    //
    // ⚠️ Because both names write the same place, an initializer that sets *both* is order-dependent: the
    // later one wins, so `with { MemoryRef = "a", Resources = [...] }` and the same two lines swapped produce
    // different projects. Nothing does that today (checked), and nothing should: set one or the other. The way out
    // is not a cleverer setter — no accessor can make two writes to one place commute — but for the callers that
    // still say `MemoryRef` to say `Resources` instead, after which this member goes. AC-485 is
    // where that starts, since the project editor is the last writer of consequence.
    public string? MemoryRef
    {
        get => Resources.FirstOrDefault(resource => resource.Role == ProjectResourceRole.Memory)?.Reference;
        init => Resources = _WithMemoryReference(Resources, value);
    }

    // `resources` with its first `ProjectResourceRole.Memory` row's
    // `ProjectResource.Reference` set to `reference`: replaced in place if such a row
    // exists, appended if it does not.
    //
    // A null or blank `reference` removes *every* Memory row, not just the first: AC2 lets
    // a project keep more than one (a local folder and a Depot project together), and `MemoryRef` is a
    // singular name for "the memory this project keeps" — if clearing it left a second Memory row standing, the
    // getter would go on reporting memory that is, from this call's point of view, supposed to be gone. Removing
    // only the first would make `with { MemoryRef = null }` lie about what it just did.
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

    // The project's logo: the path of the image the cockpit copied into its own storage when the operator picked a
    // file or gave a URL. A copy rather than the original's path (AC-162), so the card keeps its picture when the
    // source moves, is renamed, or lives on a drive that is not plugged in. Null for a project without one, which
    // shows its initial instead.
    public string? LogoPath { get; init; }

    // When a session was last started on this project, or null for one never opened. Written by the host at
    // launch, so the overview can lead with what the operator actually works on rather than the order the
    // projects happen to be stored in.
    public DateTimeOffset? LastOpenedAt { get; init; }

    // Whatever else belongs with this project, under labels the operator chose (AC-295): the repository it lives in,
    // the customer's website, a contact. Deliberately not a field per kind of information — the cockpit cannot know
    // which kinds a project needs, and each new one would otherwise cost a model change.
    //
    // Empty for most projects. Shown where a project is read rather than where it is started, and a value that is an
    // `http(s)` URL is shown as a link (see `ProjectInfoField.IsWebLink`).
    public IReadOnlyList<ProjectInfoField> AdditionalInfo { get; init; } = [];

    // Whether this project keeps any information of its own, so a surface leaves the block out rather than holding an empty space open.
    public bool HasAdditionalInfo => AdditionalInfo.Count > 0;

    // The local cache of the password that unwraps this project's shared-field envelope in Depot (AC-607). Its
    // name matches `SecretFields.ByName`, so it is encrypted at rest and scrubbed from backups the same
    // way every other credential in `cockpit.json` already is (AC-353) — no new storage mechanism.
    public string? ProjectPassword { get; init; }

    // AC-762: last known shared-project source name — set by share/bind, cleared by "Stop sharing", confirmed or
    // cleared by the next successful list. Fallback for when `IProjectOwnershipRegistry` has no live claim yet;
    // never a second source of truth, a successful list always wins.
    public string? SharedSourceName { get; init; }

    // A record's compiler-generated ToString() would otherwise print ProjectPassword in the clear — masked here
    // the same way ProjectInfoField.Mask hides a secret AdditionalInfo value (Iron Law #8). `PrintMembers`
    // (the usual record idiom) trips this SDK's IDE0051 analyzer as a false "unused member", hence ToString().
    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.Append("Project { ");
        builder.Append($"Id = {Id}, Name = {Name}, Description = {Description}, SourceDirectory = {SourceDirectory}, ");
        builder.Append($"GitUrl = {GitUrl}, DefaultProfileLabel = {DefaultProfileLabel}, BehaviorPrompt = {BehaviorPrompt}, ");
        builder.Append($"IsolateInWorktreeByDefault = {IsolateInWorktreeByDefault}, McpOverlay = {McpOverlay}, Resources = {Resources}, ");
        builder.Append($"MemoryRef = {MemoryRef}, LogoPath = {LogoPath}, LastOpenedAt = {LastOpenedAt}, AdditionalInfo = {AdditionalInfo}, ");
        builder.Append($"HasAdditionalInfo = {HasAdditionalInfo}, ProjectPassword = {(ProjectPassword is null ? null : ProjectInfoField.Mask)}, ");
        builder.Append($"Category = {Category}, PluginFields = {PluginFields}, SharedSourceName = {SharedSourceName} }}");
        return builder.ToString();
    }

    // Which category this project sits under in the manager's list (AC-618) — "Privé", "Werk", whatever the
    // operator types; null/blank groups it under "Uncategorized" instead. Always local, even for a project bound
    // to a shared Depot definition: the operator who shares a project does not get to impose their own filing on
    // everyone who opens it, the same local/portable line `ProjectResource` already draws for memory.
    //
    // Compared case-insensitively (`StringComparison.OrdinalIgnoreCase` — never the culture-sensitive
    // default, which is exactly the AC-372 class of bug: a Turkish locale's lowercase of `I` is not `i`,
    // so `"Werk"` and `"werk"` would stop matching there). This project's own text is kept exactly as
    // typed rather than rewritten to a shared casing — the group heading it shows under is what carries the
    // "shown as first typed" rule; see `ProjectSettings.CategoryOrder`.
    public string? Category { get; init; }

    // What this project is called elsewhere (AC-317), under the key the plugin that asked registered: the YouTrack
    // project it is tracked in, the repository it lives in. Where `AdditionalInfo` is what the operator
    // wants to remember, this is what a plugin resolves — a value it queries with, not a note anyone reads.
    //
    // Held by the host rather than by each plugin because three plugins ask the same question about one project, and
    // because a link must survive its plugin being uninstalled: a value under a key nothing claims is carried through
    // untouched, so reinstalling the plugin finds the project still linked.
    public IReadOnlyDictionary<string, string> PluginFields { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    // What this project is called under `key`, or null when nothing linked it there. Keys match exactly, the way plugin ids and intent actions do.
    public string? LinkedAs(string key) =>
        PluginFields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    // A new project with a generated id, mirroring `Workspace.Create`.
    public static Project Create(string name) => new(Guid.NewGuid().ToString("n"), name);
}
