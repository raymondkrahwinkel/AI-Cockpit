namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// What the operator changed on a bound project's claimed fields (AC-247) — <see cref="ISharedProjectSource.WriteBackAsync"/>'s
/// own input, the write-side mirror of <see cref="SharedProjectBinding"/>. Deliberately narrower than that record:
/// it carries only the fields <c>HostProjectField</c> lists as claimable and editable here — <c>GitUrl</c> and
/// <c>Resources</c> are read but never edited through this dialog (AC-247's own scope) and must be carried through
/// untouched by a source's own <c>WriteBackAsync</c>, never dropped because this type does not mention them.
/// </summary>
/// <param name="Name">The operator's edited name — never blank; the editor's own <c>CanSave</c> already guards that.</param>
/// <param name="Description">The operator's edited description. Null clears it.</param>
/// <param name="BehaviorPrompt">The operator's edited behaviour prompt. Null clears it.</param>
/// <param name="IsolateInWorktreeByDefault">The operator's edited worktree-isolation default.</param>
/// <param name="EnabledMcpServerNames">
/// The operator's edited MCP overlay, by server name. Null means "no opinion" (every offered server ticked) —
/// the same idiom <see cref="SharedProjectBinding.EnabledMcpServerNames"/> already carries in the read direction.
/// </param>
/// <param name="LogoEdit">
/// The operator's edit to the shared logo (AC-763) — null means untouched (a source's own <c>WriteBackAsync</c>
/// carries whatever it already has through unchanged); non-null means replaced or cleared, see
/// <see cref="SharedProjectLogoEdit"/>.
/// </param>
public sealed record SharedProjectDefinitionEdit(
    string Name,
    string? Description,
    string? BehaviorPrompt,
    bool IsolateInWorktreeByDefault,
    IReadOnlyList<string>? EnabledMcpServerNames,
    SharedProjectLogoEdit? LogoEdit = null);

/// <summary>
/// <see cref="SharedProjectDefinitionEdit.LogoEdit"/>'s own tri-state (AC-763): the field itself being null means
/// "untouched"; this record then splits the remaining two states — cleared, or replaced with new bytes.
/// </summary>
/// <param name="PngBytes">The replacement logo's PNG bytes, or null for <see cref="Cleared"/>.</param>
public sealed record SharedProjectLogoEdit(byte[]? PngBytes)
{
    public static SharedProjectLogoEdit Replace(byte[] pngBytes) => new(pngBytes);

    public static readonly SharedProjectLogoEdit Cleared = new(PngBytes: null);
}
