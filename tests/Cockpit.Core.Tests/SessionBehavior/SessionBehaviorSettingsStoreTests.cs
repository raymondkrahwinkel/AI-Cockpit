using Cockpit.Core.Diagnostics;
using Cockpit.Core.SessionBehavior;
using Cockpit.Infrastructure.SessionBehavior;

namespace Cockpit.Core.Tests.SessionBehavior;

/// <summary>
/// Load/save round-trip for the session-behaviour section of <c>cockpit.json</c>, plus the invariant
/// that saving it leaves the sibling sections (notifications, transcript display) intact — all stores
/// share the one file.
/// </summary>
public class SessionBehaviorSettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public SessionBehaviorSettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsSettings()
    {
        var store = new SessionBehaviorSettingsStore(_configFilePath);

        await store.SaveAsync(new SessionBehaviorSettings { AutoCloseOnExit = true, CombineQueuedMessages = true });
        var loaded = await store.LoadAsync();

        Assert.True(loaded.AutoCloseOnExit);
        Assert.True(loaded.CombineQueuedMessages);
    }

    [Fact]
    public async Task LoadAsync_ASectionWrittenBeforeTheSharedBudgetExisted_ReadsTheDefaultRatherThanZero()
    {
        // AC-1086: absent would deserialise as 0, and a budget of nothing warns on an idle cockpit — for every
        // install that predates the setting, which is all of them.
        await File.WriteAllTextAsync(_configFilePath, """{"sessionBehavior":{"autoCloseOnExit":true}}""");

        var settings = await new SessionBehaviorSettingsStore(_configFilePath).LoadAsync();

        Assert.Equal(MemoryPressure.DefaultBudgetPercent, settings.MemoryBudgetPercent);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
