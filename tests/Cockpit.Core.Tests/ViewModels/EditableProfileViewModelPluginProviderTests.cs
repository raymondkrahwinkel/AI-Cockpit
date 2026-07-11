using Avalonia.Controls;
using Cockpit.App.ViewModels;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Claude;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// <see cref="EditableProfileViewModel"/>'s plugin-provider arm (#45): a profile carrying a
/// <see cref="PluginProviderConfig"/> resolves its registered provider's display name and config view from
/// <see cref="IPluginProviderRegistry"/>, is neither <c>IsClaudeProvider</c> nor <c>IsLocalProvider</c>, and
/// round-trips its config view's JSON back through <see cref="EditableProfileViewModel.ToProfile"/>.
/// </summary>
public class EditableProfileViewModelPluginProviderTests
{
    [Fact]
    public void Constructor_WithAPluginProviderConfig_ResolvesTheRegisteredProviderAndBuildsItsConfigView()
    {
        var registry = new PluginProviderRegistry();
        var configView = new FakePluginProviderConfigView("""{"ApiKey":"secret"}""");
        registry.Register(_Registration("gemini-provider.gemini", "Gemini (OpenAI-compatible)", configView));
        var profile = new ClaudeProfile("gemini", ConfigDir: "", ProviderConfig: new PluginProviderConfig("gemini-provider.gemini", """{"ApiKey":"secret"}"""));
        var providers = SessionProviderCatalog.AllProviders(registry);

        var editable = new EditableProfileViewModel(profile, isLoggedIn: false, providers: providers, pluginProviderRegistry: registry);

        editable.IsPluginProvider.Should().BeTrue();
        editable.IsClaudeProvider.Should().BeFalse();
        editable.IsLocalProvider.Should().BeFalse();
        editable.SelectedProvider.Label.Should().Be("Gemini (OpenAI-compatible)");
        editable.PluginConfigView.Should().Be(configView);
    }

    [Fact]
    public void ToProfile_WithAPluginProvider_SerializesThePluginConfigViewIntoAPluginProviderConfig()
    {
        var registry = new PluginProviderRegistry();
        var configView = new FakePluginProviderConfigView("""{"ApiKey":"secret","Model":"gemini-2.5-flash"}""");
        registry.Register(_Registration("gemini-provider.gemini", "Gemini", configView));
        var profile = new ClaudeProfile("gemini", ConfigDir: "", ProviderConfig: new PluginProviderConfig("gemini-provider.gemini", "{}"));
        var providers = SessionProviderCatalog.AllProviders(registry);
        var editable = new EditableProfileViewModel(profile, isLoggedIn: false, providers: providers, pluginProviderRegistry: registry);

        var saved = editable.ToProfile();

        saved.ProviderConfig.Should().BeOfType<PluginProviderConfig>();
        var pluginConfig = (PluginProviderConfig)saved.ProviderConfig!;
        pluginConfig.ProviderId.Should().Be("gemini-provider.gemini");
        pluginConfig.ConfigJson.Should().Be("""{"ApiKey":"secret","Model":"gemini-2.5-flash"}""");
    }

    [Fact]
    public void IsValid_WhenThePluginConfigViewFailsValidation_IsFalse()
    {
        var registry = new PluginProviderRegistry();
        var configView = new FakePluginProviderConfigView(json: null, isValid: false);
        registry.Register(_Registration("gemini-provider.gemini", "Gemini", configView));
        var profile = new ClaudeProfile("gemini", ConfigDir: "", ProviderConfig: new PluginProviderConfig("gemini-provider.gemini", "{}"));
        var providers = SessionProviderCatalog.AllProviders(registry);
        var editable = new EditableProfileViewModel(profile, isLoggedIn: false, providers: providers, pluginProviderRegistry: registry);

        editable.IsValid.Should().BeFalse();
    }

    [Fact]
    public void OnSelectedProviderChanged_WhenAddingAProfileAndPickingAPluginProvider_BuildsAFreshConfigViewFromTheRegistry()
    {
        var registry = new PluginProviderRegistry();
        var configView = new FakePluginProviderConfigView(json: null);
        registry.Register(_Registration("gemini-provider.gemini", "Gemini", configView));
        var providers = SessionProviderCatalog.AllProviders(registry);
        var editable = new EditableProfileViewModel(
            new ClaudeProfile("new profile", string.Empty), isLoggedIn: false, canChooseProvider: true, providers: providers, pluginProviderRegistry: registry);

        editable.SelectedProvider = providers.Single(option => option.PluginProviderId == "gemini-provider.gemini");

        editable.IsPluginProvider.Should().BeTrue();
        editable.PluginConfigView.Should().Be(configView);
    }

    private static SessionProviderRegistration _Registration(string providerId, string displayName, IPluginProviderConfigView configView) => new(
        ProviderId: providerId,
        DisplayName: displayName,
        CreateDriverFactory: _ => throw new NotSupportedException("Not exercised by these view-model tests."),
        Capabilities: new PluginSessionCapabilities(false, false, false, false, false),
        CreateConfigView: _ => configView);

    /// <summary>A minimal <see cref="IPluginProviderConfigView"/> test double — <see cref="View"/> is a plain
    /// <see cref="Panel"/> rather than anything touching Avalonia platform services (Cursor/ToolTip), which
    /// this headless xunit process cannot resolve.</summary>
    private sealed class FakePluginProviderConfigView(string? json, bool isValid = true) : IPluginProviderConfigView
    {
        public Control View { get; } = new Panel();

        public bool TryGetConfigJson(out string configJson)
        {
            if (!isValid)
            {
                configJson = string.Empty;
                return false;
            }

            configJson = json ?? "{}";
            return true;
        }
    }
}
