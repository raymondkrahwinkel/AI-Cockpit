using System.Text.Json;
using Cockpit.Plugins.Abstractions;
namespace Cockpit.Plugin.Autopilot.Tests;

public class AutopilotRunContextTests
{
    // Round-trips through JSON the way the host's real storage does, so an unset key reads back as "not set".
    private sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);

        public T? Get<T>(string key) => _data.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;

        public void Set<T>(string key, T value) => _data[key] = JsonSerializer.Serialize(value);

        public void SetSecret(string key, string value) => Set(key, value);

        public string? GetSecret(string key) => Get<string>(key);
    }

    private static AutopilotPlan _SourcePlan() =>
        new("Do the work", new AutopilotPlanSource("YouTrack", "AC-191", "A title"), []);

    [Fact]
    public void ValidatorCeoRequest_NamesItsOwnPermissionMode_SoAProfileSavedInBypassCannotDecideIt()
    {
        // Confinement is only granted if the provider vouches for it, and a permission-based provider stops
        // vouching in a bypass mode. Naming a mode here drops whatever the CEO profile has stored — without it, a
        // profile saved on bypassPermissions makes the host refuse the validator and the run waits on a CEO that never starts (AC-191).
        var storage = new FakeStorage();
        var settings = new AutopilotSettings(storage);
        settings.SetAutonomyMode("bypassPermissions");

        var request = AutopilotRunContext.ValidatorCeoRequest(settings, "/runs/worktree", _SourcePlan(), "run-1");

        Assert.False(string.IsNullOrWhiteSpace(request.PermissionMode));
        // Coerced away from bypass by AutopilotSettings (AC-209), so even a stored bypass cannot reach the driver here.
        Assert.Equal(AutopilotSettings.DefaultAutonomyMode, request.PermissionMode);
        Assert.NotEqual("bypassPermissions", request.PermissionMode);
    }

}
