using Cockpit.Core.Layout;
using Cockpit.Core.Terminal;
using Cockpit.Infrastructure.Layout;
using Cockpit.Infrastructure.Terminal;
using FluentAssertions;

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

        settings.FontFamily.Should().Be("Cascadia Mono, Consolas, monospace");
        settings.FontSize.Should().Be(13);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsSettings()
    {
        var store = new TerminalSettingsStore(_configFilePath);

        await store.SaveAsync(new TerminalSettings { FontFamily = "JetBrains Mono", FontSize = 16 });
        var loaded = await store.LoadAsync();

        loaded.FontFamily.Should().Be("JetBrains Mono");
        loaded.FontSize.Should().Be(16);
    }

    [Fact]
    public async Task SaveAsync_LeavesTheOtherSectionsIntact()
    {
        var layoutStore = new LayoutSettingsStore(_configFilePath);
        await layoutStore.SaveAsync(new LayoutSettings { SingleSessionLayout = true });

        var terminalStore = new TerminalSettingsStore(_configFilePath);
        await terminalStore.SaveAsync(new TerminalSettings { FontFamily = "Fira Code", FontSize = 20 });

        (await layoutStore.LoadAsync()).SingleSessionLayout.Should().BeTrue();
        (await terminalStore.LoadAsync()).FontFamily.Should().Be("Fira Code");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
