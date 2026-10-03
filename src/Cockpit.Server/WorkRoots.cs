using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Clones;
using Cockpit.Core.Abstractions.Worktrees;

namespace Cockpit.Server;

// AC-1464: in the image the CLIs run as `agent`, which cannot enter the state root the server keeps 0700, and the
// default clone and worktree roots lie under it. The image names roots both users share in COCKPIT_CLONE_ROOT and
// COCKPIT_WORKTREE_ROOT; a blank root takes them, so a fresh `compose up` works. A root the operator chose stays.
internal static class WorkRoots
{
    public const string CloneRootVariable = "COCKPIT_CLONE_ROOT";

    public const string WorktreeRootVariable = "COCKPIT_WORKTREE_ROOT";

    public static async Task ApplyAsync(IServiceProvider services, ILogger logger)
    {
        if (_FromEnvironment(CloneRootVariable) is { } cloneRoot)
        {
            var store = services.GetRequiredService<ICloneSettingsStore>();
            var settings = await store.LoadAsync();
            if (string.IsNullOrWhiteSpace(settings.Root))
            {
                await store.SaveAsync(settings with { Root = cloneRoot });
                logger.LogInformation("Clone root set to {Root} from {Variable}.", cloneRoot, CloneRootVariable);
            }
        }

        if (_FromEnvironment(WorktreeRootVariable) is { } worktreeRoot)
        {
            var store = services.GetRequiredService<IWorktreeSettingsStore>();
            var settings = await store.LoadAsync();
            if (string.IsNullOrWhiteSpace(settings.Root))
            {
                await store.SaveAsync(settings with { Root = worktreeRoot });
                logger.LogInformation("Worktree root set to {Root} from {Variable}.", worktreeRoot, WorktreeRootVariable);
            }
        }
    }

    // Only an absolute path: a relative one would resolve against wherever the server happened to start.
    private static string? _FromEnvironment(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value && Path.IsPathRooted(value) ? value : null;
}
