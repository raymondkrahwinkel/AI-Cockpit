using Cockpit.Core.Worktrees;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `WorktreeSettings` in the `worktreeSettings` section of `cockpit.json` (AC-85). Separate from the `worktrees` registry section, which lists the worktrees themselves.
internal sealed class WorktreeSettingsEntry
{
    public string? Root { get; set; }

    public static WorktreeSettingsEntry FromDomain(WorktreeSettings settings) => new() { Root = settings.Root };

    public WorktreeSettings ToDomain() => new() { Root = Root };
}
