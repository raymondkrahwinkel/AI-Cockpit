using Cockpit.Core.Abstractions;
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
    // Marking it leaves a sibling section intact (AC-1506 folded that second test in here).
    [Fact]
    public async Task TheCompletionMarker_IsAbsentUntilMarked_ThenCarriesTheLatestVersionRatherThanAFlag_AndLeavesOtherSectionsIntact()
    {
        var store = new FirstRunWizardStateStore(ConfigPath);
        var layoutStore = new Layout.LayoutSettingsStore(ConfigPath);
        await layoutStore.SaveAsync(new Cockpit.Core.Layout.LayoutSettings { SingleSessionLayout = true });

        Assert.Null(await store.GetCompletedVersionAsync());

        var resume = new FirstRunWizardResumeState(
            25,
            "start-fresh",
            [new FirstRunProviderChoice("claude-provider", "Claude Code", [new FirstRunSessionProviderChoice("claude", "Claude")])],
            "claude",
            "Claude",
            "{\"model\":\"sonnet\"}");
        await store.SaveResumeStateAsync(resume);
        var restored = Assert.IsType<FirstRunWizardResumeState>(await store.GetResumeStateAsync());
        Assert.Equal(25, restored.StartAtOrder);
        Assert.Equal("start-fresh", restored.StartChoiceId);
        Assert.Equivalent(new[] { new FirstRunProviderChoice("claude-provider", "Claude Code", [new FirstRunSessionProviderChoice("claude", "Claude")]) }, restored.Providers);
        Assert.Equal("claude", restored.AssistantProviderId);
        Assert.Equal("{\"model\":\"sonnet\"}", restored.AssistantConfigJson);

        await store.MarkCompletedAsync(1);

        Assert.Equal(1, await store.GetCompletedVersionAsync());
        Assert.Null(await store.GetResumeStateAsync());
        Assert.True((await layoutStore.LoadAsync()).SingleSessionLayout);

        await store.MarkCompletedAsync(2);

        Assert.Equal(2, await store.GetCompletedVersionAsync());
    }
}
