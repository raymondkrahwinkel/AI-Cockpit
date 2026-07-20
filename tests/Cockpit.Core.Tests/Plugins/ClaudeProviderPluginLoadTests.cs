using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.App.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// End-to-end loader proof for the Claude provider plugin (Fase 4): loads the real compiled plugin through the
/// actual <see cref="PluginActivator"/>/<see cref="PluginLoadContext"/> and asserts type-identity holds, its
/// metadata is right, and it registers both of Claude's routes under the id <c>claude</c> — the TTY route via
/// <see cref="ICockpitHost.AddTtyProvider"/> and the SDK/session-driver route (control-protocol permissions, weg A)
/// via <see cref="ICockpitHost.AddSessionProvider"/>, the seams the running app's plugin manager exercises.
/// Mirrors <see cref="CliAgentProviderPluginLoadTests"/>.
/// </summary>
public class ClaudeProviderPluginLoadTests
{
    [Fact]
    public void ActivatesAndRegistersBothClaudeRoutes_WhenBuilt()
    {
        var folder = _LocatePluginOutput();
        folder.Should().NotBeNull("the Claude provider plugin is built as a test dependency");

        var manifestJson = File.ReadAllText(Path.Combine(folder!, "plugin.json"));
        PluginManifest.TryParse(manifestJson, out var manifest, out _).Should().BeTrue();
        manifest.Should().NotBeNull();

        var hash = PluginHash.Compute(File.ReadAllBytes(Path.Combine(folder, manifest!.EntryAssembly)));
        var discovered = new DiscoveredPlugin(folder, "claude-provider", manifest, hash, PluginLoadDecision.Load);

        var activator = new PluginActivator(NullLogger<PluginActivator>.Instance);
        var plugin = activator.Activate(discovered);

        plugin.Should().NotBeNull();
        plugin!.Metadata.Id.Should().Be("claude-provider");
        plugin.Metadata.DisplayName.Should().Be("Claude (bundled)");

        plugin.ConfigureServices(new ServiceCollection());

        var host = new RecordingHost();
        plugin.Initialize(host);

        // Both routes register under the id the resolver routes a Claude profile to.
        host.TtyProviders.Should().ContainSingle();
        var ttyRegistration = host.TtyProviders.Single();
        ttyRegistration.ProviderId.Should().Be("claude");
        ttyRegistration.DisplayName.Should().Be("Claude");
        ttyRegistration.Options.Should().Contain(option => option.Key == "permission-mode");
        ttyRegistration.Options.Should().Contain(option => option.Key == "model");
        ttyRegistration.Options.Should().Contain(option => option.Key == "effort");
        ttyRegistration.CreateProvider(host.Services).Should().NotBeNull();

        // The SDK/session-driver route (weg A): control-protocol permissions, so it reports SupportsPermissions and
        // mints a driver factory through the real activator.
        host.SessionProviders.Should().ContainSingle();
        var sessionRegistration = host.SessionProviders.Single();
        sessionRegistration.ProviderId.Should().Be("claude");
        sessionRegistration.DisplayName.Should().Be("Claude");
        sessionRegistration.Capabilities.SupportsPermissions.Should().BeTrue();
        // Vision rides the registration capabilities, which is the object the host honours: SessionDriverFactory
        // builds the driver adapter from registration.Capabilities, not the driver instance's own. Regression guard
        // for the pasted image being gated off ("provider does not support image input") when this was left false.
        sessionRegistration.Capabilities.SupportsVision.Should().BeTrue();
        sessionRegistration.Options.Should().Contain(option => option.Key == "permission-mode");
        sessionRegistration.Options.Should().Contain(option => option.Key == "model");
        sessionRegistration.CreateDriverFactory(host.Services).Should().NotBeNull();
        // CreateConfigView is not exercised here — it builds a real Avalonia Control (see CliAgentProviderPluginLoadTests).

        plugin.Dispose();
    }

    private static string? _LocatePluginOutput()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidateRoot = Path.Combine(directory.FullName, "plugins-dev", "Cockpit.Plugin.ClaudeProvider", "bin");
            if (Directory.Exists(candidateRoot))
            {
                var dll = Directory
                    .EnumerateFiles(candidateRoot, "Cockpit.Plugin.ClaudeProvider.dll", SearchOption.AllDirectories)
                    .FirstOrDefault();
                return dll is null ? null : Path.GetDirectoryName(dll);
            }

            directory = directory.Parent;
        }

        return null;
    }

    private sealed class RecordingHost : ICockpitHost
    {
        public List<SessionProviderRegistration> SessionProviders { get; } = [];

        public List<TtyProviderRegistration> TtyProviders { get; } = [];

        public IServiceProvider Services { get; } = new ServiceCollection().BuildServiceProvider();

        public ICockpitActions Actions { get; } = new NoActions();

        public IPluginStorage Storage { get; } = new MemoryStorage();

        public void AddSettings(Func<Control> createView)
        {
        }

        public void AddSideMenuButton(string title, Action onInvoke)
        {
        }

        public void AddSideMenuSection(string title, Func<Control> createView)
        {
        }

        public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) => Task.CompletedTask;

        public void AddSessionProvider(SessionProviderRegistration registration) => SessionProviders.Add(registration);

        public void AddTtyProvider(TtyProviderRegistration registration) => TtyProviders.Add(registration);
    }

    private sealed class NoActions : ICockpitActions
    {
        public bool HasActiveSession => false;

        public Task InjectIntoActiveSessionAsync(string text) => Task.CompletedTask;

        public Task SetClipboardTextAsync(string text) => Task.CompletedTask;
    }

    private sealed class MemoryStorage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _values = [];

        public T? Get<T>(string key) => _values.TryGetValue(key, out var value) ? (T?)value : default;

        public void Set<T>(string key, T value) => _values[key] = value;
    }
}
