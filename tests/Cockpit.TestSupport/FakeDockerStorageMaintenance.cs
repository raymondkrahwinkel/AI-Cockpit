using Cockpit.Core.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.TestSupport;

public sealed class FakeDockerStorageMaintenance : IDockerStorageMaintenance
{
    public string Summary => "Fake storage maintenance";
    public IReadOnlyList<string> History => [];
    public bool CanCompact => false;
    public event Action? Changed { add { } remove { } }
    public void Start() { }
    public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RequestCompactionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public static class MachineStorageTestServices
{
    public static void FakeMachineStorage(this IServiceCollection services) =>
        services.AddSingleton<IDockerStorageMaintenance>(new FakeDockerStorageMaintenance());
}
