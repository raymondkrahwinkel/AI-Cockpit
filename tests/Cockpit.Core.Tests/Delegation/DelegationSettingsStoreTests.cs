using Cockpit.Core.Debugging;
using Cockpit.Core.Delegation;
using Cockpit.Infrastructure.Debugging;
using Cockpit.Infrastructure.Delegation;
using FluentAssertions;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// Load/save round-trip for the delegation section of <c>cockpit.json</c> (AC-40), plus the invariant every store
/// keeps: saving one section leaves its siblings alone.
/// </summary>
public class DelegationSettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public DelegationSettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task LoadAsync_NoConfigFile_LeavesTheOrchestratorMcpOn()
    {
        var settings = await new DelegationSettingsStore(_configFilePath).LoadAsync();

        settings.McpEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsTheToggle()
    {
        var store = new DelegationSettingsStore(_configFilePath);

        await store.SaveAsync(new DelegationSettings { McpEnabled = false });

        (await store.LoadAsync()).McpEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_LeavesAnotherSectionAlone()
    {
        await new DebugSettingsStore(_configFilePath).SaveAsync(new DebugSettings { ShowDebugControls = true });

        await new DelegationSettingsStore(_configFilePath).SaveAsync(new DelegationSettings { McpEnabled = false });

        (await new DebugSettingsStore(_configFilePath).LoadAsync()).ShowDebugControls.Should().BeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
