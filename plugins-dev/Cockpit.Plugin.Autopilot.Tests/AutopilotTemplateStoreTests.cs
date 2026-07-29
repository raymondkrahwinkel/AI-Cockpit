using System.Text.Json;
using Cockpit.Plugins.Abstractions;
namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The persisted template store (AC-189): the operator's own templates and their edits (overrides) of the plugin
/// templates survive a restart through the plugin's storage, while the plugin registrations themselves stay in memory.
/// The combined list is the registrations with any override applied, followed by the user templates, each with the
/// right edit/delete flags.
/// </summary>
public class AutopilotTemplateStoreTests
{
    /// <summary>An in-memory <see cref="IPluginStorage"/> that round-trips through JSON, the way the host's real storage does.</summary>
    private sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);

        public T? Get<T>(string key) => _data.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;

        public void Set<T>(string key, T value) => _data[key] = JsonSerializer.Serialize(value);

        public void SetSecret(string key, string value) => Set(key, value);

        public string? GetSecret(string key) => Get<string>(key);
    }

    private static RegisteredAutopilotTemplate _Registration(string id, string name, string body) =>
        new("acme", new PluginAutopilotTemplate(id, name, body));

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

    [Fact]
    public void List_CombinesRegistrationsThenUserTemplates_WithTheRightFlags()
    {
        var store = new AutopilotTemplateStore(new FakeStorage());
        store.UpsertUserTemplate(AutopilotTemplate.ForUser("user.mine", "Mine", "body"));

        var combined = store.List([_Registration("acme.triage", "Triage", "Triage {{issue.id}}")]);

        Assert.Equal(2, System.Linq.Enumerable.Count(combined));
        var plugin = combined[0];
        Assert.Equal("acme.triage", plugin.Id);
        Assert.Equal(AutopilotTemplateOrigin.Plugin, plugin.Origin);
        Assert.Equal("acme", plugin.OwnerPluginId);
        Assert.True(plugin.Editable);     // plugin templates are editable...
        Assert.False(plugin.Deletable);   // ...but never deletable
        Assert.Equal("user.mine", combined[1].Id);
        Assert.Equal(AutopilotTemplateOrigin.User, combined[1].Origin);
    }

    [Fact]
    public void Override_WinsOverTheRegistration_AndSurvivesARestart()
    {
        var storage = new FakeStorage();
        var store = new AutopilotTemplateStore(storage);
        store.UpsertOverride(new AutopilotTemplateOverride("acme.triage", "My triage", "My {{issue.id}} brief", ["issue.id"]));

        var restored = new AutopilotTemplateStore(storage);
        var template = Assert.Single(restored.List([_Registration("acme.triage", "Triage", "Triage {{issue.id}}")]));

        Assert.Equal("My triage", template.Name);                  // the override's fields win...
        Assert.Equal("My {{issue.id}} brief", template.Body);
        Assert.NotNull(template.RequiredPlaceholders);
        Assert.Equal("issue.id", Assert.Single(template.RequiredPlaceholders));
        Assert.Equal(AutopilotTemplateOrigin.Plugin, template.Origin); // ...while it stays a plugin template
        Assert.Equal("acme", template.OwnerPluginId);
    }

    [Fact]
    public void ResetOverride_DropsTheEditOnly_LeavingTheRegistrationToShowThrough()
    {
        var store = new AutopilotTemplateStore(new FakeStorage());
        store.UpsertOverride(new AutopilotTemplateOverride("acme.triage", "My triage", "edited", null));

        store.ResetOverride("acme.triage");

        var template = Assert.Single(store.List([_Registration("acme.triage", "Triage", "Triage {{issue.id}}")]));
        Assert.Equal("Triage", template.Name);                 // the original registration is back...
        Assert.Equal("Triage {{issue.id}}", template.Body);
        Assert.Single(store.List([_Registration("acme.triage", "Triage", "Triage {{issue.id}}")])); // ...the template itself was never removed
    }

    [Fact]
    public void DeleteUserTemplate_RemovesAUserTemplate_ButIsANoOpForAPluginId()
    {
        var store = new AutopilotTemplateStore(new FakeStorage());
        store.UpsertUserTemplate(AutopilotTemplate.ForUser("user.mine", "Mine", "body"));

        store.DeleteUserTemplate("acme.triage"); // a plugin id — not a user template, so nothing is removed
        Assert.Equal(2, System.Linq.Enumerable.Count(store.List([_Registration("acme.triage", "Triage", "t")])));

        store.DeleteUserTemplate("user.mine");   // the user template — gone
        Assert.Equal("acme.triage", Assert.Single(store.List([_Registration("acme.triage", "Triage", "t")]).Select(t => t.Id)));
    }

    [Fact]
    public void List_MergesRegistrationsWithOverridesAndUserTemplates_InOneCombinedList()
    {
        // The glue the plan flow and the settings section both read (AC-189): the in-memory plugin registrations with any
        // persisted override applied, then the operator's own persisted templates — one list, right order, right flags.
        var storage = new FakeStorage();
        var store = new AutopilotTemplateStore(storage);
        store.UpsertOverride(new AutopilotTemplateOverride("acme.triage", "My triage", "My {{issue.id}}", null));
        store.UpsertUserTemplate(AutopilotTemplate.ForUser("user.mine", "Mine", "Do {{input.thing}}"));

        // A fresh store over the same storage proves the persisted half survives a restart while the registrations are
        // re-supplied in memory.
        var combined = new AutopilotTemplateStore(storage).List(
        [
            _Registration("acme.triage", "Triage", "Triage {{issue.id}}"),
            _Registration("acme.release", "Release", "Cut a release"),
        ]);

        Assert.Equal(new[] { "acme.triage", "acme.release", "user.mine" }, combined.Select(t => t.Id));

        Assert.Equal("My triage", combined[0].Name);                       // the override won over the registration...
        Assert.Equal("My {{issue.id}}", combined[0].Body);
        Assert.Equal(AutopilotTemplateOrigin.Plugin, combined[0].Origin);  // ...while it stayed a plugin template
        Assert.False(combined[0].Deletable);

        Assert.Equal("Release", combined[1].Name);                         // an un-overridden registration shows through as-is
        Assert.Equal(AutopilotTemplateOrigin.Plugin, combined[1].Origin);

        Assert.Equal(AutopilotTemplateOrigin.User, combined[2].Origin);    // the operator's own, deletable
        Assert.True(combined[2].Deletable);
    }

    [Fact]
    public void UpsertUserTemplate_RefusesANonUserTemplate()
    {
        var store = new AutopilotTemplateStore(new FakeStorage());

        var act = () => store.UpsertUserTemplate(AutopilotTemplate.ForPlugin("acme", new PluginAutopilotTemplate("acme.triage", "Triage", "body")));

        Assert.Throws<ArgumentException>(act);
    }

    [Fact]
    public void UpsertUserTemplate_ReplacesAnExistingTemplateWithTheSameId()
    {
        var store = new AutopilotTemplateStore(new FakeStorage());
        store.UpsertUserTemplate(AutopilotTemplate.ForUser("user.mine", "First", "one"));
        store.UpsertUserTemplate(AutopilotTemplate.ForUser("user.mine", "Second", "two"));

        var template = Assert.Single(store.List([]));
        Assert.Equal("Second", template.Name);
        Assert.Equal("two", template.Body);
    }
}
