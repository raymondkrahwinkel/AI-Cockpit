using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1201: <see cref="PaneWorkspaceDirectory.WorkspaceIdsByPane"/> is reached off the UI thread every 5s, via
/// <c>RefreshClaimCollisionsAsync</c>'s <c>Task.Run</c> — while <c>CockpitViewModel.Sessions</c> is UI-thread-owned
/// state a real dispatcher tick can be mutating at the same moment.
/// </summary>
/// <remarks>
/// A real dispatcher is the point, same as <see cref="AssistantAgentGatewayUiThreadTests"/>: without one pumping
/// on its own thread, "off the UI thread" collapses into the inline branch and this proves nothing.
/// </remarks>
[Collection("avalonia")]
public class PaneWorkspaceDirectoryUiThreadTests
{
    [Fact]
    public async Task WorkspaceIdsByPane_CalledFromAThreadpoolThread_WhileTheUiThreadMutatesSessions_DoesNotThrow()
    {
        var (directory, cockpit, deskId) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            var desk = Workspace.Create("Sessions", WorkspaceType.Sessions);
            cockpit.Workspaces.Settings = new WorkspaceSettings { Workspaces = [desk], ActiveWorkspaceId = desk.Id };

            for (var i = 0; i < 100; i++)
            {
                cockpit.Sessions.Add(new SessionViewModel { WorkspaceId = desk.Id });
            }

            return (new PaneWorkspaceDirectory(new _SingleObjectProvider(cockpit)), cockpit, desk.Id);
        });

        // Mutates from the real UI thread via a blocking Invoke, not a fire-and-forget Post: Post floods the
        // dispatcher queue and starves the reads below into UiUnavailableException, a queueing artefact of this
        // harness rather than the race AC-1201 is about. Invoke paces the mutator at the dispatcher's own rate.
        var mutating = true;
        var mutator = new Thread(() =>
        {
            while (Volatile.Read(ref mutating))
            {
                Dispatcher.UIThread.Invoke(() =>
                {
                    if (cockpit.Sessions.Count > 1)
                    {
                        cockpit.Sessions.RemoveAt(cockpit.Sessions.Count - 1);
                    }

                    cockpit.Sessions.Add(new SessionViewModel { WorkspaceId = deskId });
                });
            }
        })
        { IsBackground = true };
        mutator.Start();

        try
        {
            await Task.Run(() =>
            {
                // The premise: without this the loop below would be the inline branch, proving nothing.
                Assert.False(Dispatcher.UIThread.CheckAccess());

                for (var i = 0; i < 500; i++)
                {
                    directory.WorkspaceIdsByPane();
                }
            }).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            Volatile.Write(ref mutating, false);
        }
    }

    /// <summary>The one dependency <see cref="PaneWorkspaceDirectory"/> resolves lazily; a container is more machinery than this needs.</summary>
    private sealed class _SingleObjectProvider(object instance) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(instance) ? instance : null;
    }
}
