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

    [Fact]
    public async Task GetCompletedVersionAsync_NothingSaved_IsNull() =>
        Assert.Null(await new FirstRunWizardStateStore(ConfigPath).GetCompletedVersionAsync());

    [Fact]
    public async Task MarkCompletedAsync_ThenGetCompletedVersionAsync_RoundTripsTheVersion()
    {
        var store = new FirstRunWizardStateStore(ConfigPath);

        await store.MarkCompletedAsync(3);

        Assert.Equal(3, await store.GetCompletedVersionAsync());
    }

    // A version rather than a bool is the whole point (AC-509): a later run marking a newer version must not be
    // indistinguishable from an older one — both would just be "true" if this collapsed to a flag.
    [Fact]
    public async Task MarkCompletedAsync_TwiceWithDifferentVersions_KeepsTheLatestOne()
    {
        var store = new FirstRunWizardStateStore(ConfigPath);

        await store.MarkCompletedAsync(1);
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
