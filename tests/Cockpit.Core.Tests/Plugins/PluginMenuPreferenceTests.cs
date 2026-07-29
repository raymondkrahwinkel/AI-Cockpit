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
    public async Task LoadAllAsync_ForAPluginNobodyMoved_LeavesItAtTheDefaultPositionAndVisible()
    {
        var store = new PluginRegistrationStore(_configFilePath);
        await store.SaveAsync("youtrack", new PluginRegistration(Enabled: true, PinnedSha256: "abc"));

        var registration = (await store.LoadAllAsync())["youtrack"];

        Assert.Equal(0, registration.MenuOrder);
        Assert.False(registration.HiddenInMenu);
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

    [Fact]
    public async Task SaveAsync_AfterAMenuPreferenceWasSet_DoesNotResetIt()
    {
        var store = new PluginRegistrationStore(_configFilePath);
        await store.SaveMenuPreferenceAsync("youtrack", menuOrder: 4, hiddenInMenu: true);

        // Disabling the plugin writes the enable state and must leave the operator's menu preference standing —
        // re-enabling it later should put it back where they had it.
        await store.SaveAsync("youtrack", new PluginRegistration(Enabled: false, PinnedSha256: "abc"));
        var registration = (await store.LoadAllAsync())["youtrack"];

        Assert.False(registration.Enabled);
        Assert.Equal(4, registration.MenuOrder);
        Assert.True(registration.HiddenInMenu);
    }

    [Fact]
    public async Task SaveMenuPreferenceAsync_KeepsThePluginsOwnStoredData()
    {
        var store = new PluginRegistrationStore(_configFilePath);
        await store.SaveDataAsync("youtrack", new Dictionary<string, string> { ["token"] = "\"kept\"" });

        await store.SaveMenuPreferenceAsync("youtrack", menuOrder: 1, hiddenInMenu: false);

        var data = await store.LoadDataAsync("youtrack");
        Assert.Contains("token", data.Keys);
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
