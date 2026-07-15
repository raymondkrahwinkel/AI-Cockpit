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
/// metadata is right, and it registers Claude's TTY route via <see cref="ICockpitHost.AddTtyProvider"/> — the seam
/// the running app's plugin manager exercises. No session provider yet: the SDK/session-driver route follows in a
/// later increment. Mirrors <see cref="CliAgentProviderPluginLoadTests"/>.
/// </summary>
public class ClaudeProviderPluginLoadTests
{
    [Fact]
    public void ActivatesAndRegistersTheClaudeTtyProvider_WhenBuilt()
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

        // Increment 1 registers only the TTY route (the SDK/session-driver route follows), under the id the resolver
        // routes a Claude profile to.
        host.SessionProviders.Should().BeEmpty();
        host.TtyProviders.Should().ContainSingle();
        var ttyRegistration = host.TtyProviders.Single();
        ttyRegistration.ProviderId.Should().Be("claude");
        ttyRegistration.DisplayName.Should().Be("Claude");
        ttyRegistration.Options.Should().Contain(option => option.Key == "permission-mode");
        ttyRegistration.Options.Should().Contain(option => option.Key == "model");
        ttyRegistration.Options.Should().Contain(option => option.Key == "effort");
        ttyRegistration.CreateProvider(host.Services).Should().NotBeNull();

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
