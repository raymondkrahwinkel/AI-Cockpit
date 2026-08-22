using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

// AC-254's settings-screen half: the validation profile/model boxes default blank (follows planning) and Save
// persists them as an independent override, the same round trip the planning pair already had.
[Collection("avalonia")]
public class AutopilotSettingsControlValidationTests
{
    // What the host does on a Save click (AC-1003): stage, then run the write the view handed back.
    private static void _Save(IPluginSettingsView view)
    {
        Assert.True(view.TryStage(out var commit, out _));
        commit!();
    }

    [Fact]
    public void ValidationBoxes_StartBlank_WhenNoOverrideIsStored()
    {
        var control = _Control(new AutopilotSettings(new FakeStorage()));

        Assert.Null(control.CeoValidationProfileBox.SelectedItem);
        Assert.Equal(string.Empty, control.CeoValidationModelBox.Text);
    }

    [Fact]
    public void Save_PersistsAValidationOverride_AsItsOwnStoredValue()
    {
        var storage = new FakeStorage();
        var settings = new AutopilotSettings(storage);
        var control = _Control(settings);

        control.CeoValidationProfileBox.SelectedItem = "work-sonnet";
        control.CeoValidationModelBox.Text = "sonnet";
        _Save(control);

        // Read from a fresh instance over the same backing storage, the way the real host reopens settings next
        // time — proves Save actually wrote the override rather than only updating the in-memory settings object.
        var reloaded = new AutopilotSettings(storage);
        Assert.Equal("work-sonnet", reloaded.CeoValidationProfileLabelOverride());
        Assert.Equal("sonnet", reloaded.CeoValidationModelOverride());
    }

    [Fact]
    public void Save_WithBlankValidationModel_ClearsTheOverride()
    {
        var storage = new FakeStorage();
        var settings = new AutopilotSettings(storage);
        settings.SetCeoValidationModel("sonnet");
        var control = _Control(settings);
        Assert.Equal("sonnet", control.CeoValidationModelBox.Text);

        control.CeoValidationModelBox.Text = string.Empty;
        _Save(control);

        Assert.Null(settings.CeoValidationModelOverride());
    }

    private static AutopilotSettingsControl _Control(AutopilotSettings settings)
    {
        var host = Substitute.For<ICockpitHost>();
        host.RegisteredAutopilotTemplates.Returns([]);
        host.CreateHelpHint(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(_ => new Panel());
        return new AutopilotSettingsControl(settings, host, new AutopilotTemplateStore(new FakeStorage()));
    }

    private sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _data = new(StringComparer.Ordinal);

        public T? Get<T>(string key) => _data.TryGetValue(key, out var value) && value is T typed ? typed : default;

        public void Set<T>(string key, T value) => _data[key] = value;

        public void SetSecret(string key, string value) => Set(key, value);

        public string? GetSecret(string key) => Get<string>(key);
    }
}
