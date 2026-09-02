using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Tests.Configuration;

/// <summary>
/// The first-run wizard's completion marker (AC-509): absent before the wizard has ever run, a version once it
/// has, and — since it is a version rather than a bool — round-trips whatever version was recorded rather than
/// collapsing to a flag.
/// </summary>
public sealed class FirstRunWizardStateStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cockpit-first-run-wizard-{Guid.NewGuid():N}");

    private string ConfigPath => Path.Combine(_directory, "cockpit.json");

    public FirstRunWizardStateStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // The marker's whole life in one exercise: absent before the wizard ever ran, carrying the version once it has,
    // and — the point of AC-509 — keeping the latest rather than collapsing to a flag a second run cannot move.
    [Fact]
    public async Task TheCompletionMarker_IsAbsentUntilMarked_ThenCarriesTheLatestVersionRatherThanAFlag()
    {
        var store = new FirstRunWizardStateStore(ConfigPath);

        Assert.Null(await store.GetCompletedVersionAsync());

        await store.MarkCompletedAsync(1);

        Assert.Equal(1, await store.GetCompletedVersionAsync());

        await store.MarkCompletedAsync(2);

        Assert.Equal(2, await store.GetCompletedVersionAsync());
    }

    [Fact]
    public async Task MarkCompletedAsync_LeavesOtherSectionsIntact()
    {
        var layoutStore = new Layout.LayoutSettingsStore(ConfigPath);
        await layoutStore.SaveAsync(new Cockpit.Core.Layout.LayoutSettings { SingleSessionLayout = true });

        await new FirstRunWizardStateStore(ConfigPath).MarkCompletedAsync(1);

        Assert.True((await layoutStore.LoadAsync()).SingleSessionLayout);
    }
}
