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
}
