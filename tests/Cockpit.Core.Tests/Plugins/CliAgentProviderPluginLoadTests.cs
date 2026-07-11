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
/// End-to-end loader proof for the CLI agent (Codex) provider plugin (#45 fase B1): loads the real compiled
/// plugin through the actual <see cref="PluginActivator"/>/<see cref="PluginLoadContext"/> and asserts
/// type-identity holds, its metadata is right, and it registers its Codex session provider via
/// <see cref="ICockpitHost.AddSessionProvider"/> — the seam that would otherwise only be exercised by a
/// hand-written fake. Mirrors <see cref="GeminiProviderPluginLoadTests"/>.
/// </summary>
public class CliAgentProviderPluginLoadTests
{
    [Fact]
    public void ActivatesAndRegistersTheCodexSessionProvider_WhenBuilt()
    {
        var folder = _LocatePluginOutput();
        folder.Should().NotBeNull("the CLI agent provider plugin is built as a test dependency");

        var manifestJson = File.ReadAllText(Path.Combine(folder!, "plugin.json"));
        PluginManifest.TryParse(manifestJson, out var manifest, out _).Should().BeTrue();
        manifest.Should().NotBeNull();

        var hash = PluginHash.Compute(File.ReadAllBytes(Path.Combine(folder, manifest!.EntryAssembly)));
        var discovered = new DiscoveredPlugin(folder, "cli-agent-provider", manifest, hash, PluginLoadDecision.Load);

        var activator = new PluginActivator(NullLogger<PluginActivator>.Instance);
        var plugin = activator.Activate(discovered);

        // A non-null cast to the host's ICockpitPlugin is itself the type-identity proof.
        plugin.Should().NotBeNull();
        plugin!.Metadata.Id.Should().Be("cli-agent-provider");
        plugin.Metadata.DisplayName.Should().Be("CLI Agent Provider (Codex)");

        plugin.ConfigureServices(new ServiceCollection());

        var host = new RecordingHost();
        plugin.Initialize(host);

        host.SessionProviders.Should().ContainSingle();
        var registration = host.SessionProviders.Single();
        registration.ProviderId.Should().Be("cli-agent-provider.codex");
        registration.DisplayName.Should().Be("Codex (CLI)");
        registration.Capabilities.SupportsTools.Should().BeTrue();
        registration.Capabilities.SupportsPermissions.Should().BeFalse();

        // The driver factory is usable through the narrow plugin contract without the host ever seeing this
        // plugin's concrete types. CreateConfigView is not exercised here — it builds a real Avalonia Control,
        // which needs a running Avalonia application; this headless xunit process has none, same reason the
        // sibling plugin load tests never invoke their own AddSettings/AddSideMenuSection view factories either.
        var driverFactory = registration.CreateDriverFactory(host.Services);
        var driver = driverFactory.Create("""{"Command":"codex","WorkingDirectory":"."}""");
        driver.Should().NotBeNull();

        plugin.Dispose();
    }

    // Walks up from the test output to the repo root and finds the plugin's build output (either config).
    private static string? _LocatePluginOutput()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidateRoot = Path.Combine(directory.FullName, "plugins-dev", "Cockpit.Plugin.CliAgentProvider", "bin");
            if (Directory.Exists(candidateRoot))
            {
                var dll = Directory
                    .EnumerateFiles(candidateRoot, "Cockpit.Plugin.CliAgentProvider.dll", SearchOption.AllDirectories)
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
