using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.App.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// End-to-end loader proof for the opencode ACP provider plugin (AC-783): loads it through <see cref="PluginActivator"/>
/// and asserts it registers its session provider with real-agent (tools/permissions) capabilities.
/// </summary>
public class OpencodeProviderPluginLoadTests
{
    [Fact]
    public void ActivatesAndRegistersTheSessionProvider_WhenBuilt()
    {
        var folder = _LocatePluginOutput();
        Assert.NotNull(folder);

        var manifestJson = File.ReadAllText(Path.Combine(folder!, "plugin.json"));
        Assert.True(PluginManifest.TryParse(manifestJson, out var manifest, out _));
        Assert.NotNull(manifest);

        var hash = PluginHash.Compute(File.ReadAllBytes(Path.Combine(folder, manifest!.EntryAssembly)));
        var discovered = new DiscoveredPlugin(folder, "opencode-provider", manifest, hash, PluginLoadDecision.Load);

        var activator = new PluginActivator(NullLogger<PluginActivator>.Instance);
        var plugin = activator.Activate(discovered);

        // A non-null cast to the host's ICockpitPlugin is itself the type-identity proof.
        Assert.NotNull(plugin);
        Assert.Equal("opencode-provider", plugin!.Metadata.Id);

        plugin.ConfigureServices(new ServiceCollection());

        var host = new RecordingHost();
        plugin.Initialize(host);

        Assert.Single(host.SessionProviders);
        var registration = host.SessionProviders[0];
        Assert.Equal("opencode-provider.acp", registration.ProviderId);
        Assert.Equal("opencode (ACP)", registration.DisplayName);

        // Real-agent capabilities, unlike the chat-only OpenAiCompat provider plugins (AC-806/AC-724) —
        // this is the one criterion that actually distinguishes this ticket from those two.
        Assert.True(registration.Capabilities.SupportsTools);
        Assert.True(registration.Capabilities.SupportsPermissions);
        Assert.True(registration.Capabilities.SupportsLiveModelSwitch);

        // CreateConfigView is not exercised here — it builds a real Avalonia Control (Cursor/ToolTip), which
        // needs a running Avalonia application; this headless xunit process has none, same reason the sibling
        // plugin load tests never invoke their own config-view factories either.
        var driverFactory = registration.CreateDriverFactory(host.Services);
        var driver = driverFactory.Create("""{"Command":"opencode","ApiKey":"test-key"}""");
        Assert.NotNull(driver);

        plugin.Dispose();
    }

    // Walks up from the test output to the repo root and finds the plugin's build output (either config).
    private static string? _LocatePluginOutput()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidateRoot = Path.Combine(directory.FullName, "plugins-dev", "Cockpit.Plugin.OpencodeProvider", "bin");
            if (Directory.Exists(candidateRoot))
            {
                var dll = Directory
                    .EnumerateFiles(candidateRoot, "Cockpit.Plugin.OpencodeProvider.dll", SearchOption.AllDirectories)
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
