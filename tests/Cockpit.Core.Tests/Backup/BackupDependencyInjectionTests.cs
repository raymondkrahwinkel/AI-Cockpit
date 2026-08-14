using Microsoft.Extensions.DependencyInjection;
using Cockpit.App;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Backup;
using Cockpit.Infrastructure;

namespace Cockpit.Core.Tests.Backup;

/// <summary>
/// The Backup tab has two buttons, and buttons that do nothing are the thing this codebase refuses to ship (#70). The
/// backup service reaches the view model through an <em>optional</em> constructor parameter — which is exactly the
/// shape that compiles, runs, and quietly stays null. So the container is built the way <c>Program.cs</c> builds it,
/// and asked.
/// </summary>
public class BackupDependencyInjectionTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly,
            typeof(CockpitViewModel).Assembly);

        services.AddSessionPanes();

        return services.BuildServiceProvider();
    }

    [Fact]
    public void TheContainer_HasSomethingThatCanBackTheCockpitUp()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetService<IBackupService>());
    }

    [Fact]
    public async Task TheCockpit_IsBuiltWithIt_SoTheBackupButtonsAreNotDeadControls()
    {
        // The cockpit view model is IAsyncDisposable (it owns sessions), so the container must go the same way.
        await using var provider = BuildProvider();

        var cockpit = provider.GetRequiredService<CockpitViewModel>();

        // The service arrives through an optional constructor parameter, which is the shape that compiles, runs, and
        // quietly stays null — leaving two buttons that swallow a click. The buttons bind to this.
        Assert.True(cockpit.CanBackUp);
        Assert.False(cockpit.BackupIncludesCredentials, "an archive you can drop anywhere must not be a key ring");
    }

    /// <summary>Same shape, same risk, for the assistant-memory-only export/import buttons (AC-657).</summary>
    [Fact]
    public async Task TheCockpit_IsBuiltWithAssistantMemory_SoItsOwnBackupButtonsAreNotDeadControls()
    {
        await using var provider = BuildProvider();

        var cockpit = provider.GetRequiredService<CockpitViewModel>();

        Assert.True(cockpit.CanBackUpAssistantMemory);
    }
}
