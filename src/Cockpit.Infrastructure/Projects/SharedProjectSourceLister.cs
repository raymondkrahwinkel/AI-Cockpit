using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Infrastructure.Projects;

// AC-1374: moved from ProjectsViewModel — neither member touches view-model state, so ProjectsViewModel now calls
// these instead of carrying its own copy (its own comment on the visibility filter already said "not a second
// copy of it"; this is what keeps that true once the assistant's read gateway lives outside App).
public static class SharedProjectSourceLister
{
    // Depot reads six definitions in parallel with a 10-second per-project timeout, so 90 seconds leaves room for five batches.
    // Keep this whole-source bound for a source that hangs before it can report partial progress.
    public static TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public static async Task<SharedProjectListResult> ListWithTimeoutAsync(ISharedProjectSource source, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var listTask = source.ListAsync(timeoutCts.Token);
            var completed = await Task.WhenAny(listTask, Task.Delay(Timeout, cancellationToken)).ConfigureAwait(false);
            if (completed != listTask)
            {
                timeoutCts.Cancel();
                return SharedProjectListResult.Failed("Timed out waiting for a response.");
            }

            return await listTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return SharedProjectListResult.Failed(exception.Message);
        }
    }

    // The ids a shared project is filtered against before it counts as visible: already bound to a local project,
    // or hidden on this machine (AC-797).
    public static (HashSet<string> BoundIds, HashSet<string> HiddenIds) VisibilityFilterIds(ProjectSettings settings) =>
        (
            new HashSet<string>(
                settings.Projects
                    .SelectMany(project => project.Resources)
                    .Where(resource => resource.Role == ProjectResourceRole.Memory)
                    .Select(resource => resource.Reference),
                StringComparer.Ordinal),
            new HashSet<string>(settings.HiddenSharedProjectIds, StringComparer.Ordinal)
        );
}
