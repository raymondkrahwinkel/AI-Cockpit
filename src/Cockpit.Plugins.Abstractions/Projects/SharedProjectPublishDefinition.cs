namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// The portable snapshot of a not-yet-shared local project, offered to <see cref="ISharedProjectSource.PublishAsync"/>
/// (AC-620) — the host's own idea of "what goes to Depot" before the source's own portability/secrecy rules
/// (AC-244/AC-612) narrow it further. Deliberately carries no field a project's <c>AdditionalInfo</c> rows could
/// populate: a secret-marked field never reaches this boundary in the first place, the same "never on the wire
/// unencrypted" line <c>CockpitProjectDefinitionSecrecyTests</c> already pins on the write side.
/// </summary>
/// <param name="Name">The project's display name — never blank; the confirmation screen's own guard already ensures that.</param>
/// <param name="Description">Free-text note on what this project is. Null when the project carries none.</param>
/// <param name="GitUrl">The project's own Git source, an offer for whoever binds to it later — same idiom as <c>Project.GitUrl</c>. Null for a project with no source of its own.</param>
/// <param name="BehaviorPrompt">How the profile should behave here, same idiom as <c>Project.BehaviorPrompt</c>. Null appends nothing.</param>
/// <param name="IsolateInWorktreeByDefault">Whether new sessions here isolate in their own git worktree by default.</param>
/// <param name="EnabledMcpServerNames">Names of MCP servers new sessions here start ticked. Null means no opinion — every offered server ticked, the same idiom <see cref="SharedProjectBinding.EnabledMcpServerNames"/> already carries.</param>
/// <param name="Resources">The project's own resource rows, unfiltered — see <see cref="SharedProjectPublishResource"/>.</param>
/// <param name="LogoBytes">The project's own logo, as the cockpit's local store already holds it (AC-763) — null for a project with none.</param>
public sealed record SharedProjectPublishDefinition(
    string Name,
    string? Description,
    string? GitUrl,
    string? BehaviorPrompt,
    bool IsolateInWorktreeByDefault,
    IReadOnlyList<string>? EnabledMcpServerNames,
    IReadOnlyList<SharedProjectPublishResource> Resources,
    byte[]? LogoBytes = null);
