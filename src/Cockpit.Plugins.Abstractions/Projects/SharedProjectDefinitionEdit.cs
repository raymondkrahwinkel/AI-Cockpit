namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// What the operator changed on a bound project's claimed fields (AC-247) — <see cref="ISharedProjectSource.WriteBackAsync"/>'s
/// own input, the write-side mirror of <see cref="SharedProjectBinding"/>. Deliberately narrower than that record:
/// it carries only the fields <c>HostProjectField</c> lists as claimable and editable here — <c>GitUrl</c> and
/// <c>Resources</c> are read but never edited through this dialog (AC-247's own scope), and <c>Logo</c> is a
/// claimable field this round leaves permanently locked (no artifact-upload path yet — a source's own
/// <c>WriteBackAsync</c> is expected to carry an existing logo through untouched, never drop it because this type
/// does not mention it).
/// </summary>
/// <param name="Name">The operator's edited name — never blank; the editor's own <c>CanSave</c> already guards that.</param>
/// <param name="Description">The operator's edited description. Null clears it.</param>
/// <param name="BehaviorPrompt">The operator's edited behaviour prompt. Null clears it.</param>
/// <param name="IsolateInWorktreeByDefault">The operator's edited worktree-isolation default.</param>
/// <param name="EnabledMcpServerNames">
/// The operator's edited MCP overlay, by server name. Null means "no opinion" (every offered server ticked) —
/// the same idiom <see cref="SharedProjectBinding.EnabledMcpServerNames"/> already carries in the read direction.
/// </param>
public sealed record SharedProjectDefinitionEdit(
    string Name,
    string? Description,
    string? BehaviorPrompt,
    bool IsolateInWorktreeByDefault,
    IReadOnlyList<string>? EnabledMcpServerNames);
