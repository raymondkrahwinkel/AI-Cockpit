using Avalonia.Controls;
using Cockpit.App.ViewModels;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

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
        var profile = new SessionProfile("gemini", new PluginProviderConfig("gemini-provider.gemini", """{"ApiKey":"secret"}"""));
        var providers = SessionProviderCatalog.AllProviders(registry);

        var editable = new EditableProfileViewModel(profile, isLoggedIn: false, providers: providers, pluginProviderRegistry: registry);

        Assert.True(editable.IsPluginProvider);
        Assert.False(editable.IsClaudeProvider);
        Assert.False(editable.IsLocalProvider);
        Assert.Equal("Gemini (OpenAI-compatible)", editable.SelectedProvider.Label);
        Assert.Equal(configView, editable.PluginConfigView);
    }

    [Fact]
    public void ToProfile_WithAPluginProvider_SerializesThePluginConfigViewIntoAPluginProviderConfig()
    {
        var registry = new PluginProviderRegistry();
        var configView = new FakePluginProviderConfigView("""{"ApiKey":"secret","Model":"gemini-2.5-flash"}""");
        registry.Register(_Registration("gemini-provider.gemini", "Gemini", configView));
        var profile = new SessionProfile("gemini", new PluginProviderConfig("gemini-provider.gemini", "{}"));
        var providers = SessionProviderCatalog.AllProviders(registry);
        var editable = new EditableProfileViewModel(profile, isLoggedIn: false, providers: providers, pluginProviderRegistry: registry);

        var saved = editable.ToProfile();

        Assert.IsType<PluginProviderConfig>(saved.ProviderConfig);
        var pluginConfig = (PluginProviderConfig)saved.ProviderConfig!;
        Assert.Equal("gemini-provider.gemini", pluginConfig.ProviderId);
        Assert.Equal("""{"ApiKey":"secret","Model":"gemini-2.5-flash"}""", pluginConfig.ConfigJson);
    }

    /// <summary>
    /// A stored profile whose provider id resolves to nothing in the registry (the plugin was removed,
    /// disabled, or failed to load — a normal, lasting state, not a transient error) must not silently drop
    /// into an empty/broken config: the editor flags it as missing instead of pretending it is editable.
    /// </summary>
    [Fact]
    public void Constructor_WithAnUnregisteredProviderId_MarksTheProfileAsMissingWithNoConfigView()
    {
        var registry = new PluginProviderRegistry(); // nothing registered — simulates a removed/disabled plugin
        var profile = new SessionProfile("gemini", new PluginProviderConfig("gemini-provider.gemini", """{"ApiKey":"secret"}"""));

        var editable = new EditableProfileViewModel(profile, isLoggedIn: false, pluginProviderRegistry: registry);

        Assert.True(editable.IsPluginProvider);
        Assert.Null(editable.PluginConfigView);
        Assert.True(editable.IsPluginProviderMissing);
    }

    /// <summary>
    /// Root-cause regression for the #45 review's "orphan-profile corrupts on save" finding: previously
    /// <c>_ToProviderConfig()</c> returned <see langword="null"/> whenever <see cref="EditableProfileViewModel.PluginConfigView"/>
    /// was null, which <c>ToProfile()</c> then collapsed to a bare <see cref="SessionProfile"/> with no
    /// <see cref="ProviderConfig"/> at all — silently discarding the ProviderId and ConfigJson (and any API
    /// key inside it). It must instead hand back the original config completely unchanged.
    /// </summary>
    [Fact]
    public void ToProfile_WithAnUnregisteredProviderId_PreservesTheOriginalProviderIdAndConfigJsonUnchanged()
    {
        var registry = new PluginProviderRegistry();
        var originalConfig = new PluginProviderConfig("gemini-provider.gemini", """{"ApiKey":"super-secret","Model":"gemini-2.5-flash"}""");
        var profile = new SessionProfile("gemini", originalConfig);
        var editable = new EditableProfileViewModel(profile, isLoggedIn: false, pluginProviderRegistry: registry);

        var saved = editable.ToProfile();

        Assert.IsType<PluginProviderConfig>(saved.ProviderConfig);
        Assert.Equal(originalConfig, saved.ProviderConfig);
        Assert.Equal(SessionProvider.Plugin, saved.Provider);
    }

    [Fact]
    public void IsValid_WhenThePluginConfigViewFailsValidation_IsFalse()
    {
        var registry = new PluginProviderRegistry();
        var configView = new FakePluginProviderConfigView(json: null, isValid: false);
        registry.Register(_Registration("gemini-provider.gemini", "Gemini", configView));
        var profile = new SessionProfile("gemini", new PluginProviderConfig("gemini-provider.gemini", "{}"));
        var providers = SessionProviderCatalog.AllProviders(registry);
        var editable = new EditableProfileViewModel(profile, isLoggedIn: false, providers: providers, pluginProviderRegistry: registry);

        Assert.False(editable.IsValid);
    }

    [Fact]
    public void OnSelectedProviderChanged_WhenAddingAProfileAndPickingAPluginProvider_BuildsAFreshConfigViewFromTheRegistry()
    {
        var registry = new PluginProviderRegistry();
        var configView = new FakePluginProviderConfigView(json: null);
        registry.Register(_Registration("gemini-provider.gemini", "Gemini", configView));
        var providers = SessionProviderCatalog.AllProviders(registry);
        var editable = new EditableProfileViewModel(
            new SessionProfile("new profile", new ClaudeConfig(string.Empty)), isLoggedIn: false, canChooseProvider: true, providers: providers, pluginProviderRegistry: registry);

        editable.SelectedProvider = providers.Single(option => option.PluginProviderId == "gemini-provider.gemini");

        Assert.True(editable.IsPluginProvider);
        Assert.Equal(configView, editable.PluginConfigView);
    }

    /// <summary>
    /// AC-944 regression: a bound button re-queries <c>CanExecute</c> synchronously off <c>CanExecuteChanged</c>,
    /// mid-method, before <see cref="EditableProfileViewModel.PluginConfigView"/> was rebuilt for the new provider.
    /// Unlike the plugin-picker test above, this subscribes to that event to observe the mid-method state.
    /// </summary>
    [Fact]
    public void OnSelectedProviderChanged_OnANewProfile_DoesNotCrashWhenABoundButtonReQueriesCanExecuteMidMethod()
    {
        var registry = new PluginProviderRegistry();
        var configView = new FakePluginProviderConfigView("""{"ApiKey":"secret"}""");
        registry.Register(_Registration("openai-provider.openai", "OpenAI", configView));
        var providers = SessionProviderCatalog.AllProviders(registry);
        var loginStarter = Substitute.For<IProfileLoginStarter>();
        loginStarter.CanStartLogin(Arg.Any<SessionProfile>()).Returns(true);
        var editable = new EditableProfileViewModel(
            new SessionProfile("new profile", new ClaudeConfig(string.Empty)),
            isLoggedIn: false,
            canChooseProvider: true,
            providers: providers,
            pluginProviderRegistry: registry,
            loginStarter: loginStarter);
        // Mimics what a Button bound to LoginCommand does on Avalonia/WPF: re-query CanExecute synchronously
        // the moment CanExecuteChanged fires.
        editable.LoginCommand.CanExecuteChanged += (_, _) => editable.LoginCommand.CanExecute(null);

        editable.SelectedProvider = providers.Single(option => option.PluginProviderId == "openai-provider.openai");

        Assert.True(editable.IsPluginProvider);
        Assert.Equal(configView, editable.PluginConfigView);
        var saved = editable.ToProfile();
        Assert.IsType<PluginProviderConfig>(saved.ProviderConfig);
    }

    [Fact]
    public void PluginOptionDefaults_ArePreFilledFromTheStoredDefaults_AndRoundTripThroughToProfile()
    {
        var registry = new PluginProviderRegistry();
        registry.Register(new SessionProviderRegistration(
            ProviderId: "claude",
            DisplayName: "Claude",
            CreateDriverFactory: _ => throw new NotSupportedException("Not exercised by these view-model tests."),
            Capabilities: new PluginSessionCapabilities(true, true),
            CreateConfigView: _ => new FakePluginProviderConfigView("{}"))
        {
            Options =
            [
                new PluginSessionLaunchOption("permission-mode", "Permission mode", ["default", "plan"], "default")
                {
                    ChoiceLabels = new Dictionary<string, string> { ["default"] = "Ask permissions", ["plan"] = "Plan mode" },
                },
                new PluginSessionLaunchOption("effort", "Effort", ["low", "medium", "high"], "medium"),
            ],
        });
        var providers = SessionProviderCatalog.AllProviders(registry);
        var profile = new SessionProfile(
            "work",
            new PluginProviderConfig("claude", "{}"),
            Defaults: new ProfileDefaults("default", "sonnet", "medium")
            {
                OptionDefaults = new Dictionary<string, string> { ["permission-mode"] = "plan" },
            });

        var editable = new EditableProfileViewModel(profile, isLoggedIn: true, providers: providers, pluginProviderRegistry: registry);

        // Fase 4: a plugin profile's per-profile defaults are rendered generically from the plugin's declared options.
        // The saved default (plan) pre-fills the permission-mode editor and reads its friendly label; the un-stored
        // effort falls back to the option's own declared default (medium).
        Assert.True(editable.HasPluginOptionDefaults);
        var permission = editable.PluginOptionDefaults.Single(option => option.Key == "permission-mode");
        Assert.Equal("plan", permission.Value);
        Assert.Equal("Plan mode", permission.ChoiceItems.Single(choice => choice.Value == "plan").Label);
        Assert.Equal("medium", editable.PluginOptionDefaults.Single(option => option.Key == "effort").Value);

        // The selection is written back into the profile's option defaults on save.
        var saved = editable.ToProfile();
        Assert.Equal("plan", saved.Defaults!.OptionDefaults!["permission-mode"]);
        Assert.Equal("medium", saved.Defaults!.OptionDefaults!["effort"]);
    }

    private static SessionProviderRegistration _Registration(string providerId, string displayName, IPluginProviderConfigView configView) => new(
        ProviderId: providerId,
        DisplayName: displayName,
        CreateDriverFactory: _ => throw new NotSupportedException("Not exercised by these view-model tests."),
        Capabilities: new PluginSessionCapabilities(false, false),
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
