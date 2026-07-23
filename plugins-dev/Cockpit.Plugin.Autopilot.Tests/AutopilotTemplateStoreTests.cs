using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using FluentAssertions;

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

        var template = restored.Should().ContainSingle().Subject;
        template.Id.Should().Be("user.mine");
        template.Origin.Should().Be(AutopilotTemplateOrigin.User);
        template.Body.Should().Be("Do {{input.thing}}");
        template.RequiredPlaceholders.Should().ContainSingle().Which.Should().Be("input.thing");
        template.Editable.Should().BeTrue();
        template.Deletable.Should().BeTrue();
    }

    [Fact]
    public void List_CombinesRegistrationsThenUserTemplates_WithTheRightFlags()
    {
        var store = new AutopilotTemplateStore(new FakeStorage());
        store.UpsertUserTemplate(AutopilotTemplate.ForUser("user.mine", "Mine", "body"));

        var combined = store.List([_Registration("acme.triage", "Triage", "Triage {{issue.id}}")]);

        combined.Should().HaveCount(2);
        var plugin = combined[0];
        plugin.Id.Should().Be("acme.triage");
        plugin.Origin.Should().Be(AutopilotTemplateOrigin.Plugin);
        plugin.OwnerPluginId.Should().Be("acme");
        plugin.Editable.Should().BeTrue();     // plugin templates are editable...
        plugin.Deletable.Should().BeFalse();   // ...but never deletable
        combined[1].Id.Should().Be("user.mine");
        combined[1].Origin.Should().Be(AutopilotTemplateOrigin.User);
    }

    [Fact]
    public void Override_WinsOverTheRegistration_AndSurvivesARestart()
    {
        var storage = new FakeStorage();
        var store = new AutopilotTemplateStore(storage);
        store.UpsertOverride(new AutopilotTemplateOverride("acme.triage", "My triage", "My {{issue.id}} brief", ["issue.id"]));

        var restored = new AutopilotTemplateStore(storage);
        var template = restored.List([_Registration("acme.triage", "Triage", "Triage {{issue.id}}")]).Should().ContainSingle().Subject;

        template.Name.Should().Be("My triage");                  // the override's fields win...
        template.Body.Should().Be("My {{issue.id}} brief");
        template.RequiredPlaceholders.Should().ContainSingle().Which.Should().Be("issue.id");
        template.Origin.Should().Be(AutopilotTemplateOrigin.Plugin); // ...while it stays a plugin template
        template.OwnerPluginId.Should().Be("acme");
    }

    [Fact]
    public void ResetOverride_DropsTheEditOnly_LeavingTheRegistrationToShowThrough()
    {
        var store = new AutopilotTemplateStore(new FakeStorage());
        store.UpsertOverride(new AutopilotTemplateOverride("acme.triage", "My triage", "edited", null));

        store.ResetOverride("acme.triage");

        var template = store.List([_Registration("acme.triage", "Triage", "Triage {{issue.id}}")]).Should().ContainSingle().Subject;
        template.Name.Should().Be("Triage");                 // the original registration is back...
        template.Body.Should().Be("Triage {{issue.id}}");
        store.List([_Registration("acme.triage", "Triage", "Triage {{issue.id}}")]).Should().HaveCount(1); // ...the template itself was never removed
    }

    [Fact]
    public void DeleteUserTemplate_RemovesAUserTemplate_ButIsANoOpForAPluginId()
    {
        var store = new AutopilotTemplateStore(new FakeStorage());
        store.UpsertUserTemplate(AutopilotTemplate.ForUser("user.mine", "Mine", "body"));

        store.DeleteUserTemplate("acme.triage"); // a plugin id — not a user template, so nothing is removed
        store.List([_Registration("acme.triage", "Triage", "t")]).Should().HaveCount(2);

        store.DeleteUserTemplate("user.mine");   // the user template — gone
        store.List([_Registration("acme.triage", "Triage", "t")]).Select(t => t.Id).Should().ContainSingle().Which.Should().Be("acme.triage");
    }

    [Fact]
    public void UpsertUserTemplate_RefusesANonUserTemplate()
    {
        var store = new AutopilotTemplateStore(new FakeStorage());

        var act = () => store.UpsertUserTemplate(AutopilotTemplate.ForPlugin("acme", new PluginAutopilotTemplate("acme.triage", "Triage", "body")));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UpsertUserTemplate_ReplacesAnExistingTemplateWithTheSameId()
    {
        var store = new AutopilotTemplateStore(new FakeStorage());
        store.UpsertUserTemplate(AutopilotTemplate.ForUser("user.mine", "First", "one"));
        store.UpsertUserTemplate(AutopilotTemplate.ForUser("user.mine", "Second", "two"));

        var template = store.List([]).Should().ContainSingle().Subject;
        template.Name.Should().Be("Second");
        template.Body.Should().Be("two");
    }
}
