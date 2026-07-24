using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;

namespace Cockpit.App.ViewModels;

/// <summary>
/// A project store that remembers nothing, for the Avalonia previewer's parameterless
/// <see cref="ProjectsViewModel"/>. The previewer has no DI container and must never touch the operator's real
/// <c>cockpit.json</c> — rendering a design-time surface is not a reason to read or write their config.
/// </summary>
internal sealed class DesignTimeProjectStore : IProjectStore
{
    private ProjectSettings _settings = ProjectSettings.Empty;

    public Task<ProjectSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_settings);

    public Task SaveAsync(ProjectSettings settings, CancellationToken cancellationToken = default)
    {
        _settings = settings;
        return Task.CompletedTask;
    }
}
