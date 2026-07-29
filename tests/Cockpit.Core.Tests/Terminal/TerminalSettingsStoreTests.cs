using Cockpit.Core.Layout;
using Cockpit.Core.Terminal;
using Cockpit.Infrastructure.Layout;
using Cockpit.Infrastructure.Terminal;

namespace Cockpit.Core.Tests.Terminal;

/// <summary>
/// Load/save round-trip for the terminal section of <c>cockpit.json</c> (#40 — global TTY font
/// family/size), plus the invariant that saving it leaves a sibling section (layout) intact.
/// </summary>
public class TerminalSettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public TerminalSettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task LoadAsync_NoConfigFile_ReturnsDefaults()
    {
        var store = new TerminalSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        Assert.Equal("Cascadia Mono, Consolas, monospace", settings.FontFamily);
        Assert.Equal(13, settings.FontSize);
        Assert.Empty(settings.Shell);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsSettings()
    {
        var store = new TerminalSettingsStore(_configFilePath);

        await store.SaveAsync(new TerminalSettings { FontFamily = "JetBrains Mono", FontSize = 16, Shell = "pwsh" });
        var loaded = await store.LoadAsync();

        Assert.Equal("JetBrains Mono", loaded.FontFamily);
        Assert.Equal(16, loaded.FontSize);
        Assert.Equal("pwsh", loaded.Shell);
    }

    [Fact]
    public async Task SaveAsync_LeavesTheOtherSectionsIntact()
    {
        var layoutStore = new LayoutSettingsStore(_configFilePath);
        await layoutStore.SaveAsync(new LayoutSettings { SingleSessionLayout = true });

        var terminalStore = new TerminalSettingsStore(_configFilePath);
        await terminalStore.SaveAsync(new TerminalSettings { FontFamily = "Fira Code", FontSize = 20 });

        Assert.True((await layoutStore.LoadAsync()).SingleSessionLayout);
        Assert.Equal("Fira Code", (await terminalStore.LoadAsync()).FontFamily);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
