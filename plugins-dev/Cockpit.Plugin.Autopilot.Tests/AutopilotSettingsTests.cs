using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using FluentAssertions;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// <see cref="AutopilotSettings"/> — the AC-149 settings foundation: every field resolves project override →
/// global → default, and a change raises the signal a live surface listens to.
/// </summary>
public class AutopilotSettingsTests
{
    /// <summary>An in-memory <see cref="IPluginStorage"/> that round-trips through JSON, the way the host's real storage does — so a null override reads back as "not set".</summary>
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

        settings.GraceTimerMinutes().Should().Be(5);
        settings.MaxSelfFixAttempts().Should().Be(2);
        settings.Gate(GateKind.Security).Should().Be(GateMode.Hard);
        settings.Gate(GateKind.Verify).Should().Be(GateMode.Skip);
        settings.CommentMirroring().Should().Be(CommentLevel.QuestionsAndMilestones);
        settings.DefaultProfileLabel().Should().BeNull();

        settings.SetGraceTimerMinutes(12);
        settings.SetGate(GateKind.Verify, GateMode.Hard);
        settings.SetDefaultProfileLabel("Work");
        settings.SetCommentMirroring(CommentLevel.Full);

        settings.GraceTimerMinutes().Should().Be(12);
        settings.Gate(GateKind.Verify).Should().Be(GateMode.Hard);
        settings.DefaultProfileLabel().Should().Be("Work");
        settings.CommentMirroring().Should().Be(CommentLevel.Full);
    }

    [Fact]
    public void ProjectOverride_WinsOverGlobal_AndClearingFallsBack()
    {
        var settings = new AutopilotSettings(new FakeStorage());
        const string project = "/home/me/repo";

        settings.SetGate(GateKind.Security, GateMode.Hard);
        settings.SetGate(GateKind.Security, GateMode.Skip, project);

        settings.Gate(GateKind.Security).Should().Be(GateMode.Hard);
        settings.Gate(GateKind.Security, project).Should().Be(GateMode.Skip);

        settings.ClearProjectGate(GateKind.Security, project);

        settings.Gate(GateKind.Security, project).Should().Be(GateMode.Hard);
    }

    [Fact]
    public void AnUnsetProject_FollowsTheGlobalValue()
    {
        var settings = new AutopilotSettings(new FakeStorage());
        settings.SetMaxSelfFixAttempts(7);

        settings.MaxSelfFixAttempts("/some/other/repo").Should().Be(7);
    }

    [Fact]
    public void ABlankProjectProfileOverride_DoesNotBlankTheGlobal()
    {
        var settings = new AutopilotSettings(new FakeStorage());
        const string project = "/home/me/repo";
        settings.SetDefaultProfileLabel("Work");

        settings.SetDefaultProfileLabel(null, project);

        settings.DefaultProfileLabel(project).Should().Be("Work");
    }

    [Fact]
    public void AutonomyMode_DefaultsToBypass_ThenRoundTrips()
    {
        var settings = new AutopilotSettings(new FakeStorage());

        settings.AutonomyMode().Should().Be(AutopilotSettings.DefaultAutonomyMode);

        settings.SetAutonomyMode("acceptEdits");
        settings.AutonomyMode().Should().Be("acceptEdits");
    }

    [Fact]
    public void StageMapping_DefaultsToUnset_ThenRoundTrips()
    {
        var settings = new AutopilotSettings(new FakeStorage());

        settings.StageFor(AutopilotRunPhase.MergeReady).Should().BeNull();

        settings.SetStageFor(AutopilotRunPhase.MergeReady, "In Review");
        settings.StageFor(AutopilotRunPhase.MergeReady).Should().Be("In Review");
    }

    [Fact]
    public void Changed_FiresOnEverySet()
    {
        var settings = new AutopilotSettings(new FakeStorage());
        var fired = 0;
        settings.Changed += () => fired++;

        settings.SetGraceTimerMinutes(9);
        settings.SetGate(GateKind.Conventions, GateMode.Hard, "/repo");
        settings.ClearProjectGate(GateKind.Conventions, "/repo");

        fired.Should().Be(3);
    }
}
