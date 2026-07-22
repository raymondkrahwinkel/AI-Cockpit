using Cockpit.Plugins.Abstractions.Workspaces;
using FluentAssertions;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The fail-closed isolation rule (AC-174, Raymond 2026-07-22): a run isolates its steps in a worktree unless the host
/// <em>positively</em> reported the folder is not a git repository. Kept in one place so a security-relevant decision
/// cannot drift; an inconclusive answer (an older host, a failed probe) must never drop the confinement guard.
/// </summary>
public class AutopilotRunEnvironmentTests
{
    [Fact]
    public void IsolateFor_AGitRepository_Isolates()
    {
        AutopilotRunEnvironment.IsolateFor(GitDirectoryStatus.Repository).Should().BeTrue();
    }

    [Fact]
    public void IsolateFor_ANonRepository_DoesNotIsolate()
    {
        // The one case that runs without isolation — a plain folder the host confirmed is not a git repository.
        AutopilotRunEnvironment.IsolateFor(GitDirectoryStatus.NotARepository).Should().BeFalse();
    }

    [Fact]
    public void IsolateFor_AnUnknownStatus_Isolates_FailClosed()
    {
        // An older host or a failed probe answers Unknown — it must be treated as needing isolation, never as a licence
        // to run free, so the guard is never dropped by an inconclusive answer.
        AutopilotRunEnvironment.IsolateFor(GitDirectoryStatus.Unknown).Should().BeTrue();
    }
}
