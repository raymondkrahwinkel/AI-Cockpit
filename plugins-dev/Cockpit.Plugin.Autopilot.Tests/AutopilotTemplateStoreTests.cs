using System.Text.Json;
using Cockpit.Plugins.Abstractions;
namespace Cockpit.Plugin.Autopilot.Tests;

// The persisted template store (AC-189): the operator's own templates and their edits (overrides) of the plugin
// templates survive a restart through storage, while the plugin registrations stay in memory. The combined list
// is the registrations with any override applied, followed by the user templates, each with the right edit/delete flags.
public class AutopilotTemplateStoreTests
{
    // An in-memory `IPluginStorage` that round-trips through JSON, the way the host's real storage does.
    private sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);

        public T? Get<T>(string key) => _data.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;

        public void Set<T>(string key, T value) => _data[key] = JsonSerializer.Serialize(value);

        public void SetSecret(string key, string value) => Set(key, value);

        public string? GetSecret(string key) => Get<string>(key);
    }

    [Fact]
    public void UserTemplate_RoundTripsThroughStorage_AcrossARestart()
    {
        var storage = new FakeStorage();
        var store = new AutopilotTemplateStore(storage);
        store.UpsertUserTemplate(AutopilotTemplate.ForUser("user.mine", "Mine", "Do {{input.thing}}", ["input.thing"]));

        // A fresh store over the same storage is the restart.
        var restored = new AutopilotTemplateStore(storage).List([]);

        var template = Assert.Single(restored);
        Assert.Equal("user.mine", template.Id);
        Assert.Equal(AutopilotTemplateOrigin.User, template.Origin);
        Assert.Equal("Do {{input.thing}}", template.Body);
        Assert.NotNull(template.RequiredPlaceholders);
        Assert.Equal("input.thing", Assert.Single(template.RequiredPlaceholders));
        Assert.True(template.Editable);
        Assert.True(template.Deletable);
    }
}
