using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Projects;

namespace Cockpit.App.Services;

// AC-2281: the Projects page's dialogs in the remote window. The ordinary project editor opens over the server's own
// profiles and saves there while it waits, so it answers null: the page has nothing left to store, and the server's
// list, read again after the write, brings the project in. Sharing and folders belong to the server, so those ask nothing.
internal sealed class RemoteProjectDialogs(
    string server,
    Func<IRemoteServer?> current,
    Func<ProjectDialogViewModel, Task> present,
    Func<string, string, string, Task<bool>> confirm) : IProjectDialogs
{
    public async Task<Project?> ShowProjectDialogAsync(Project? project, string? sharedSourceKey = null)
    {
        if (current() is not { } target)
        {
            return null;
        }

        var projects = target.Projects;
        var categories = projects.Current.Settings.Projects
            .Select(stored => stored.Category)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        await present(ProjectDialogViewModel.ForServer(project, server, await _ProfilesAsync(target), categories, async edited =>
            project is null
                ? await projects.AddNewProjectAsync(edited)
                : await projects.UpdateStoredProjectAsync(edited) ?? throw new InvalidOperationException($"{server} no longer has this project.")));
        return null;
    }

    public Task<SharedProjectBindResult?> ShowSharedProjectBindingDialogAsync(string sharedProjectId, string sourceName, string sourceKey) =>
        Task.FromResult<SharedProjectBindResult?>(null);

    public Task<Project?> ShowShareProjectDialogAsync(Project project, IReadOnlyList<string> publishSourceKeys) => Task.FromResult<Project?>(null);

    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

    public Task<bool> ShowConfirmationDialogAsync(string title, string message, string confirmLabel = "Remove") => confirm(title, message, confirmLabel);

    // An operate key may not list profiles; the editor then offers none and the server keeps what it has.
    private static async Task<IReadOnlyList<string>> _ProfilesAsync(IRemoteServer target)
    {
        try
        {
            return [.. (await target.Profiles.ListAsync()).Select(profile => profile.Label)];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [];
        }
    }
}
