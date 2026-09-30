using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The left-menu preference per plugin (#72): where it sits, and whether it shows there at all. Persisted apart
/// from the enable/consent state, because moving a plugin down the menu says nothing about whether it may run —
/// and the two writes must not overwrite each other, which is what these cover.
/// </summary>
public class PluginMenuPreferenceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public PluginMenuPreferenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task SaveMenuPreferenceAsync_RoundTripsOrderAndVisibility()
    {
        var store = new PluginRegistrationStore(_configFilePath);
        await store.SaveAsync("youtrack", new PluginRegistration(Enabled: true, PinnedSha256: "abc"));

        await store.SaveMenuPreferenceAsync("youtrack", menuOrder: 3, hiddenInMenu: true);
        var registration = (await store.LoadAllAsync())["youtrack"];

        Assert.Equal(3, registration.MenuOrder);
        Assert.True(registration.HiddenInMenu);
    }

    [Fact]
    public async Task SaveMenuPreferenceAsync_LeavesTheEnableAndConsentStateAlone()
    {
        var store = new PluginRegistrationStore(_configFilePath);
        await store.SaveAsync("youtrack", new PluginRegistration(Enabled: true, PinnedSha256: "the-consented-hash"));

        await store.SaveMenuPreferenceAsync("youtrack", menuOrder: 2, hiddenInMenu: true);
        var registration = (await store.LoadAllAsync())["youtrack"];

        // Hiding a plugin from the menu is not a quieter way of disabling it: it keeps running, and it keeps the
        // consent the operator gave it.
        Assert.True(registration.Enabled);
        Assert.Equal("the-consented-hash", registration.PinnedSha256);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
