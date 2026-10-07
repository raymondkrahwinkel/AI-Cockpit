using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Composition;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1491: a new profile on a plugin provider whose config panel does not validate yet (OpenAI, before a model is
/// typed) threw from <c>CanStartLogin</c> while the provider was being picked, and Remove on that row crashed.
/// </summary>
[Collection("avalonia")]
public class Ac1491PluginProfileConfigTests
{
    private const string ProviderId = "openai";

    [Fact]
    public Task NewOpenAiProfile_KeepsItsModelAndBaseUrl_AfterApplyAndReopen() => HeadlessAvalonia.RunAsync(async () =>
    {
        var store = new _MemoryStore();
        var dialog = _Dialog(store);
        await dialog.LoadAsync();
        var window = new ManageProfilesDialog { DataContext = dialog };
        window.Show();
        try
        {
            dialog.AddProfileCommand.Execute(null);
            window.UpdateLayout();
            var picker = window.GetVisualDescendants().OfType<ComboBox>()
                .First(box => box.ItemsSource is IReadOnlyList<SessionProviderOption>);
            picker.SelectedItem = dialog.Profiles[0].Providers.First(option => option.PluginProviderId == ProviderId);
            window.UpdateLayout();

            Assert.False(DataValidationErrors.GetHasErrors(picker));
            var view = Assert.IsType<_ConfigView>(dialog.Profiles[0].PluginConfigView);
            view.Model.Text = "qwen3-27b";
            view.BaseUrl.Text = "https://inference.example/v1";
            dialog.Profiles[0].Label = "hetzner";

            Assert.True(await dialog.PersistAsync());
        }
        finally
        {
            window.Close();
        }

        var reopened = _Dialog(store);
        await reopened.LoadAsync();
        var config = Assert.IsType<PluginProviderConfig>(Assert.Single(reopened.Profiles).ToProfile().ProviderConfig);
        using var json = JsonDocument.Parse(config.ConfigJson);
        Assert.Equal("qwen3-27b", json.RootElement.GetProperty("Model").GetString());
        Assert.Equal("https://inference.example/v1", json.RootElement.GetProperty("BaseUrl").GetString());
    });

    [Fact]
    public void RemovingAHalfFilledOpenAiProfile_DoesNotThrow() => HeadlessAvalonia.Run(() =>
    {
        var dialog = _Dialog(new _MemoryStore());
        var window = new ManageProfilesDialog { DataContext = dialog };
        window.Show();
        try
        {
            dialog.AddProfileCommand.Execute(null);
            var row = dialog.Profiles[0];
            row.SelectedProvider = row.Providers.First(option => option.PluginProviderId == ProviderId);
            window.UpdateLayout();

            Assert.False(row.CanStartLogin);
            dialog.RemoveProfileCommand.Execute(null);
            dialog.ConfirmRemoveCommand.Execute(null);
            window.UpdateLayout();

            Assert.Empty(dialog.Profiles);
        }
        finally
        {
            window.Close();
        }
    });

    // A row that starts on a provider without a panel kept that config as its fallback; after a switch to OpenAI it
    // handed Claude's config back for a row the editor shows as OpenAI.
    [Fact]
    public void ARowSwitchedToOpenAi_NeverReportsThePreviousProvidersConfig() => HeadlessAvalonia.Run(() =>
    {
        var dialog = _Dialog(new _MemoryStore(), claudeHasPanel: false);
        dialog.AddProfileCommand.Execute(null);
        var row = dialog.Profiles[0];
        row.SelectedProvider = row.Providers.First(option => option.PluginProviderId == ProviderId);

        var config = Assert.IsType<PluginProviderConfig>(row.ToProfile().ProviderConfig);
        Assert.Equal(ProviderId, config.ProviderId);
        Assert.False(row.IsValid);
    });

    private static ManageProfilesDialogViewModel _Dialog(ISessionProfileStore store, bool claudeHasPanel = true)
    {
        // Claude too, with a panel of its own as in the app: a new row starts on Claude, and that panel is what leaves
        // the row with no config to fall back to once the operator switches to OpenAI.
        var registrations = new[] { ProviderId, ClaudePluginProfile.ProviderId }
            .Select(id => new SessionProviderRegistration(
                id,
                id,
                _ => throw new NotSupportedException("No session is started in these tests."),
                new PluginSessionCapabilities(SupportsTools: true, SupportsPermissions: true)))
            .ToList();
        var registry = Substitute.For<IPluginProviderRegistry>();
        foreach (var registration in registrations)
        {
            registry.Resolve(registration.ProviderId).Returns(registration);
        }

        registry.Registrations.Returns(registrations);

        var views = new PluginProviderConfigViews();
        views.Register(ProviderId, existing => new _ConfigView(existing));
        if (claudeHasPanel)
        {
            views.Register(ClaudePluginProfile.ProviderId, _ => new _ConfigView("""{"Model":"sonnet","BaseUrl":"-"}"""));
        }

        // The real login flows: their CanStartLogin is what evaluated ToProfile() on every provider change.
        var loginFlows = Substitute.For<ISessionLoginFlows>();
        loginFlows.CanStartLogin(Arg.Any<SessionProfile>()).Returns(false);

        return new ManageProfilesDialogViewModel(
            store,
            Substitute.For<IProfileLoginChecker>(),
            pluginProviderRegistry: new PluginRegistrations(registry),
            loginStarter: loginFlows,
            pluginConfigViews: views);
    }

    private sealed class _MemoryStore : ISessionProfileStore
    {
        private IReadOnlyList<SessionProfile> _profiles = [];

        public Task<IReadOnlyList<SessionProfile>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_profiles);

        public Task SaveAsync(IReadOnlyList<SessionProfile> profiles, CancellationToken cancellationToken = default)
        {
            _profiles = profiles;
            return Task.CompletedTask;
        }
    }

    // Shaped like the OpenAI-compatible panel: it validates only once a model and a base URL are filled in.
    private sealed class _ConfigView : IPluginProviderConfigView
    {
        public TextBox Model { get; } = new();

        public TextBox BaseUrl { get; } = new();

        public Control View { get; }

        public _ConfigView(string? existing)
        {
            if (existing is { Length: > 0 })
            {
                using var json = JsonDocument.Parse(existing);
                Model.Text = json.RootElement.GetProperty("Model").GetString();
                BaseUrl.Text = json.RootElement.GetProperty("BaseUrl").GetString();
            }

            View = new StackPanel { Children = { Model, BaseUrl } };
        }

        public bool TryGetConfigJson(out string configJson)
        {
            if (string.IsNullOrWhiteSpace(Model.Text) || string.IsNullOrWhiteSpace(BaseUrl.Text))
            {
                configJson = string.Empty;
                return false;
            }

            configJson = JsonSerializer.Serialize(new { Model = Model.Text.Trim(), BaseUrl = BaseUrl.Text.Trim() });
            return true;
        }
    }
}
