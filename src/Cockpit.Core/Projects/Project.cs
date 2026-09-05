using System.Collections.ObjectModel;
using System.Text;

namespace Cockpit.Core.Projects;

// AC-1013: What a session works on (AC-158) — source folder, MCP servers, starting profile, worktree isolation;
// a project *uses* a profile by label, never extends it, so a second codebase needs no near-identical profile.
// `Id`: Stable id, never shown. `Name`: display name, renamable, free to collide with another project's name.
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

    // AC-1071: which assistant/persona this project's sessions run as, overriding `SessionProfile.Assistant`.
    // Always local, never shared (see `Category` for the same rule): a colleague binding this project keeps
    // their own assistant. Null/blank falls back to the profile's.
    public string? Assistant { get; init; }

    // Whether new sessions here isolate in their own git worktree (AC-85) when `SourceDirectory` is
    // a repository. A default only: worktree stays a per-session choice, still overridable in the dialog.
    public bool IsolateInWorktreeByDefault { get; init; }

    // Which MCP servers its sessions see, as a change on top of the global registry rather than a list of its own — see `ProjectMcpOverlay`.
    public ProjectMcpOverlay McpOverlay { get; init; } = ProjectMcpOverlay.None;

    // AC-1013: Whatever else a project's sessions read, follow or look things up in (AC-483) — memory folder,
    // Depot project, tomorrow an instruction file; see ProjectResource/ProjectResourceRole. Insertion order.
    public IReadOnlyList<ProjectResource> Resources { get; init; } = [];

    // AC-1013: Where this project's memory lives, kept separate from SourceDirectory (what it knows vs. what
    // it's made of). Mirrors the first Resources Memory row rather than its own storage (AC-483 legacy shim);
    // both names write the same place, so setting MemoryRef and Resources together is order-dependent (last wins).
    public string? MemoryRef
    {
        get => Resources.FirstOrDefault(resource => resource.Role == ProjectResourceRole.Memory)?.Reference;
        init => Resources = _WithMemoryReference(Resources, value);
    }

    // Sets `resources`'s first Memory row's Reference to `reference` (replaced in place, or appended).
    // AC-1013: A null/blank reference removes *every* Memory row, not just the first — MemoryRef is a singular
    // name for "the memory this project keeps", so `with { MemoryRef = null }` must not leave a stale row.
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

    // AC-1013: The logo the cockpit copied into its own storage (AC-162) — a copy, not the original's path,
    // so the card keeps its picture when the source moves or is unplugged. Null shows the initial instead.
    public string? LogoPath { get; init; }

    // When a session was last started on this project, or null for one never opened. Written by the host at
    // launch, so the overview can lead with what the operator actually works on rather than the order the
    // projects happen to be stored in.
    public DateTimeOffset? LastOpenedAt { get; init; }

    // AC-491: the work this project offers to start, so a first session begins from a choice rather than an empty
    // prompt box. Insertion order, and empty for a project that offers none — which behaves exactly as it did
    // before this list existed.
    public IReadOnlyList<ProjectJob> Jobs { get; init; } = [];

    // AC-1013: Whatever else belongs with this project, under operator-chosen labels (AC-295) — deliberately
    // not a field per kind of info, so a new kind never costs a model change. Empty for most projects.
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
        builder.Append($"HasAdditionalInfo = {HasAdditionalInfo}, Jobs = {Jobs}, ");
        builder.Append($"ProjectPassword = {(ProjectPassword is null ? null : ProjectInfoField.Mask)}, ");
        builder.Append($"Category = {Category}, Assistant = {Assistant}, PluginFields = {PluginFields}, SharedSourceName = {SharedSourceName} }}");
        return builder.ToString();
    }

    // AC-1013: Category shown in the manager's list (AC-618); null/blank groups under "Uncategorized". Always
    // local, even for a shared project. Compared with OrdinalIgnoreCase (AC-372: never culture-sensitive default,
    // e.g. Turkish "I"/"i"); own text kept as typed, see ProjectSettings.CategoryOrder for the shared casing.
    public string? Category { get; init; }

    // AC-1013: What this project is called elsewhere (AC-317) under the registering plugin's key — a value a
    // plugin resolves, unlike the operator-facing AdditionalInfo. Held by the host, not each plugin, so a link
    // survives its plugin being uninstalled and reinstalled.
    public IReadOnlyDictionary<string, string> PluginFields { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    // What this project is called under `key`, or null when nothing linked it there. Keys match exactly, the way
    // plugin ids and intent actions do. A value may itself name several identifiers (AC-884) — this hands back
    // only the first, unchanged for every existing caller; use `LinkedAsAll` for the rest.
    public string? LinkedAs(string key) =>
        PluginFields.TryGetValue(key, out var value) ? ProjectLinkValues.Split(value).FirstOrDefault() : null;

    // Every identifier this project is called under `key` (AC-884) — the plural of `LinkedAs`, for a plugin that
    // can act on more than one at once (a YouTrack project field naming several prefixes). Empty under the same
    // conditions `LinkedAs` answers null for.
    public IReadOnlyList<string> LinkedAsAll(string key) =>
        PluginFields.TryGetValue(key, out var value) ? ProjectLinkValues.Split(value) : [];

    // A new project with a generated id, mirroring `Workspace.Create`.
    public static Project Create(string name) => new(Guid.NewGuid().ToString("n"), name);
}
