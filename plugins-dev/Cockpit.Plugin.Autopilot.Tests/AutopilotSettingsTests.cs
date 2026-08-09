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
    public void MaxConsultsPerStep_DefaultsToThree_ThenRoundTrips()
    {
        var settings = new AutopilotSettings(new FakeStorage());

        // AC-201 loop-cap default.
        Assert.Equal(3, settings.MaxConsultsPerStep());

        settings.SetMaxConsultsPerStep(5);
        Assert.Equal(5, settings.MaxConsultsPerStep());

        const string project = "/home/me/repo";
        settings.SetMaxConsultsPerStep(1, project);
        Assert.Equal(1, settings.MaxConsultsPerStep(project));
        Assert.Equal(5, settings.MaxConsultsPerStep());
    }

    [Fact]
    public void ProjectOverride_WinsOverGlobal()
    {
        var settings = new AutopilotSettings(new FakeStorage());
        const string project = "/home/me/repo";

        settings.SetCostStrategy(AutopilotCostStrategy.Balanced);
        settings.SetCostStrategy(AutopilotCostStrategy.QualityFirst, project);

        Assert.Equal(AutopilotCostStrategy.Balanced, settings.CostStrategy());
        Assert.Equal(AutopilotCostStrategy.QualityFirst, settings.CostStrategy(project));
    }

    [Fact]
    public void AnUnsetProject_FollowsTheGlobalValue()
    {
        var settings = new AutopilotSettings(new FakeStorage());
        settings.SetMaxSelfFixAttempts(7);

        Assert.Equal(7, settings.MaxSelfFixAttempts("/some/other/repo"));
    }

    [Fact]
    public void ABlankProjectStringOverride_DoesNotBlankTheGlobal()
    {
        var settings = new AutopilotSettings(new FakeStorage());
        const string project = "/home/me/repo";
        settings.SetCeoProfileLabel("work");

        settings.SetCeoProfileLabel(null, project);

        Assert.Equal("work", settings.CeoProfileLabel(project));
    }

    [Fact]
    public void CeoValidation_UnsetFallsBackToThePlanningPair()
    {
        // AC-254: before an operator ever touches the validation override, a run must behave exactly as it did when
        // planning and validation shared one pair.
        var settings = new AutopilotSettings(new FakeStorage());
        settings.SetCeoProfileLabel("work");
        settings.SetCeoModel("opus");

        Assert.Equal("work", settings.CeoValidationProfileLabel());
        Assert.Equal("opus", settings.CeoValidationModel());
        Assert.Null(settings.CeoValidationProfileLabelOverride());
        Assert.Null(settings.CeoValidationModelOverride());
    }

    [Fact]
    public void CeoValidation_Override_WinsOverThePlanningPair_Independently()
    {
        // The mutation this ticket exists to prove: changing only the validation pair must not move planning, and
        // changing only planning must not move an already-set validation override.
        var settings = new AutopilotSettings(new FakeStorage());
        settings.SetCeoProfileLabel("work-opus");
        settings.SetCeoModel("opus");
        settings.SetCeoValidationProfileLabel("work-sonnet");
        settings.SetCeoValidationModel("sonnet");

        Assert.Equal("work-opus", settings.CeoProfileLabel());
        Assert.Equal("opus", settings.CeoModel());
        Assert.Equal("work-sonnet", settings.CeoValidationProfileLabel());
        Assert.Equal("sonnet", settings.CeoValidationModel());

        // Changing planning afterwards must not touch the validation override already set.
        settings.SetCeoModel("haiku");
        Assert.Equal("sonnet", settings.CeoValidationModel());

        // Changing validation afterwards must not touch planning.
        settings.SetCeoValidationModel("opus");
        Assert.Equal("haiku", settings.CeoModel());
    }

    [Fact]
    public void CeoValidation_ProjectOverride_WinsOverGlobalOverride_ThenPlanning()
    {
        var settings = new AutopilotSettings(new FakeStorage());
        const string project = "/home/me/repo";
        settings.SetCeoProfileLabel("work");
        settings.SetCeoValidationProfileLabel("global-validation");
        settings.SetCeoValidationProfileLabel("project-validation", project);

        Assert.Equal("global-validation", settings.CeoValidationProfileLabel());
        Assert.Equal("project-validation", settings.CeoValidationProfileLabel(project));
    }

    [Fact]
    public void AutonomyMode_DefaultsToAcceptEdits_ThenRoundTripsAConfiningMode()
    {
        var settings = new AutopilotSettings(new FakeStorage());

        Assert.Equal(AutopilotSettings.DefaultAutonomyMode, settings.AutonomyMode());
        Assert.Equal("acceptEdits", AutopilotSettings.DefaultAutonomyMode);

        settings.SetAutonomyMode("plan");
        Assert.Equal("plan", settings.AutonomyMode());
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

    [Fact]
    public void Changed_FiresOnEverySet()
    {
        var settings = new AutopilotSettings(new FakeStorage());
        var fired = 0;
        settings.Changed += () => fired++;

        settings.SetMaxSelfFixAttempts(9);
        settings.SetCostStrategy(AutopilotCostStrategy.QualityFirst, "/repo");
        settings.SetCeoProfileLabel("work");

        Assert.Equal(3, fired);
    }

    [Fact]
    public void ExecutableStage_UnsetPerTracker_FallsBackToThatTrackersDefault()
    {
        var settings = new AutopilotSettings(new FakeStorage());

        Assert.Equal("Ready", settings.ExecutableStage("youtrack"));
        Assert.Equal("ready", settings.ExecutableStage("github-issues"));
        // A tracker Autopilot ships no default for gates on nothing until its operator names a stage — better than
        // guessing a name and refusing every item on a tracker whose vocabulary we do not know.
        Assert.Equal(string.Empty, settings.ExecutableStage("jira"));
    }

    [Fact]
    public void ExecutableStage_SetBlank_TurnsTheGateOffRatherThanRestoringTheDefault()
    {
        var settings = new AutopilotSettings(new FakeStorage());

        settings.SetExecutableStage("youtrack", string.Empty);

        Assert.Equal(string.Empty, settings.ExecutableStage("youtrack"));
    }

    [Fact]
    public void ExecutableStage_IsKeptPerTracker()
    {
        var settings = new AutopilotSettings(new FakeStorage());

        settings.SetExecutableStage("youtrack", "Refined");

        Assert.Equal("Refined", settings.ExecutableStage("youtrack"));
        Assert.Equal("ready", settings.ExecutableStage("github-issues"));
    }
}
