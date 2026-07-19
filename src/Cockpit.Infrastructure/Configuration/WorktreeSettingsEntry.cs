using Cockpit.Core.Worktrees;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of <see cref="WorktreeSettings"/> in the <c>worktreeSettings</c> section of <c>cockpit.json</c> (AC-85). Separate from the <c>worktrees</c> registry section, which lists the worktrees themselves.</summary>
internal sealed class WorktreeSettingsEntry
{
    public string? Root { get; set; }

    public static WorktreeSettingsEntry FromDomain(WorktreeSettings settings) => new() { Root = settings.Root };

    public WorktreeSettings ToDomain() => new() { Root = Root };
}
