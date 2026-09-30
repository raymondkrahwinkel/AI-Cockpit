using System.Text.Json;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot.Tests;

// `AutopilotSettings`: every field resolves project override → global → default, and a change raises the
// signal a live surface listens to.
public class AutopilotSettingsTests
{
    // An in-memory `IPluginStorage` that round-trips through JSON, the way the host's real storage does — so a null override reads back as "not set".
    private sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);

        public T? Get<T>(string key) => _data.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;

        public void Set<T>(string key, T value) => _data[key] = JsonSerializer.Serialize(value);

        public void SetSecret(string key, string value) => Set(key, value);

        public string? GetSecret(string key) => Get<string>(key);
    }

    [Fact]
    public void GlobalValues_FallBackToDefaults_ThenRoundTrip()
    {
        var settings = new AutopilotSettings(new FakeStorage());

        Assert.Equal(2, settings.MaxSelfFixAttempts());
        Assert.Equal(AutopilotCostStrategy.Balanced, settings.CostStrategy());
        Assert.Null(settings.CeoProfileLabel());
        Assert.Null(settings.CeoModel());

        settings.SetMaxSelfFixAttempts(4);
        settings.SetCostStrategy(AutopilotCostStrategy.CostFirst);
        settings.SetCeoProfileLabel("work");
        settings.SetCeoModel("opus");

        Assert.Equal(4, settings.MaxSelfFixAttempts());
        Assert.Equal(AutopilotCostStrategy.CostFirst, settings.CostStrategy());
        Assert.Equal("work", settings.CeoProfileLabel());
        Assert.Equal("opus", settings.CeoModel());
    }

    [Fact]
    public void AutonomyMode_CoercesAStoredBypassPermissions_ToTheConfiningDefault()
    {
        // AC-209: a legacy stored bypassPermissions (from the AC-152 era) would disable a Claude step's worktree
        // confinement and get every Claude step of the run refused by the isolation gate — so it is coerced away.
        var settings = new AutopilotSettings(new FakeStorage());

        settings.SetAutonomyMode("bypassPermissions");

        Assert.Equal(AutopilotSettings.DefaultAutonomyMode, settings.AutonomyMode());
    }

    [Fact]
    public void AutonomyMode_CoercesABypassPermissions_ProjectOverrideToo()
    {
        // AC-209: the coercion holds for a per-project override, not just the global value — no persisted bypass, at any
        // scope, can silently block a run.
        var settings = new AutopilotSettings(new FakeStorage());
        const string project = "/home/me/repo";

        settings.SetAutonomyMode("acceptEdits");
        settings.SetAutonomyMode("bypassPermissions", project);

        Assert.Equal(AutopilotSettings.DefaultAutonomyMode, settings.AutonomyMode(project));
        Assert.Equal("acceptEdits", settings.AutonomyMode());
    }

    [Theory]
    [InlineData("Refined")]
    // Blank turns the gate off rather than restoring the tracker's default — the operator saying "no stage gate here"
    // has to survive a read, or the setting can never be cleared.
    [InlineData("")]
    public void ExecutableStage_SetOnOneTracker_IsKeptThereVerbatim_AndNowhereElse(string stage)
    {
        var settings = new AutopilotSettings(new FakeStorage());

        settings.SetExecutableStage("youtrack", stage);

        Assert.Equal(stage, settings.ExecutableStage("youtrack"));
        Assert.Equal("ready", settings.ExecutableStage("github-issues"));
    }
}
