using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.App.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Plugins.Abstractions;
using FluentAssertions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// End-to-end loader proof (#42, reworked in #48 to a side-menu button mirroring
/// <see cref="GitHubIssuesPluginLoadTests"/>): loads the real compiled YouTrack plugin through the actual
/// <see cref="PluginActivator"/> / <see cref="PluginLoadContext"/> and asserts type-identity holds (the
/// plugin's ICockpitPlugin resolves to the host's copy — the cast would be null otherwise), its metadata is
/// right, and its settings + side-menu-button contributions register (no inline side-menu section anymore —
/// the button opens the issues dialog directly, like GitHub Issues). The test project builds the plugin (a
/// ReferenceOutputAssembly=false project reference), so its output is always present.
/// </summary>
public class YouTrackPluginLoadTests
{
    [Fact]
    public void ActivatesAndContributes_WhenBuilt()
    {
        var folder = _LocatePluginOutput();
        folder.Should().NotBeNull("the YouTrack plugin is built as a test dependency");

        var manifestJson = File.ReadAllText(Path.Combine(folder!, "plugin.json"));
        PluginManifest.TryParse(manifestJson, out var manifest, out _).Should().BeTrue();
        manifest.Should().NotBeNull();

        var hash = PluginHash.Compute(File.ReadAllBytes(Path.Combine(folder, manifest!.EntryAssembly)));
        var discovered = new DiscoveredPlugin(folder, "youtrack", manifest, hash, PluginLoadDecision.Load);

        var activator = new PluginActivator(NullLogger<PluginActivator>.Instance);
        var plugin = activator.Activate(discovered);

        // A non-null cast to the host's ICockpitPlugin is itself the type-identity proof.
        plugin.Should().NotBeNull();
        plugin!.Metadata.Id.Should().Be("youtrack");
        plugin.Metadata.DisplayName.Should().Be("YouTrack");

        plugin.ConfigureServices(new ServiceCollection());

        var host = new RecordingHost();
        plugin.Initialize(host);

        host.SettingsRegistered.Should().Be(1);
        host.SideButtons.Should().ContainSingle().Which.Should().Be("YouTrack");
        host.SideSections.Should().BeEmpty();

        plugin.Dispose();
    }

    // Walks up from the test output to the repo root and finds the plugin's build output (either config).
    private static string? _LocatePluginOutput()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidateRoot = Path.Combine(directory.FullName, "plugins-dev", "Cockpit.Plugin.YouTrack", "bin");
            if (Directory.Exists(candidateRoot))
            {
                var dll = Directory
                    .EnumerateFiles(candidateRoot, "Cockpit.Plugin.YouTrack.dll", SearchOption.AllDirectories)
                    .FirstOrDefault();
                return dll is null ? null : Path.GetDirectoryName(dll);
            }

            directory = directory.Parent;
        }

        return null;
    }

    private sealed class RecordingHost : ICockpitHost
    {
        public int SettingsRegistered { get; private set; }

        public List<string> SideButtons { get; } = [];

        public List<string> SideSections { get; } = [];

        public IServiceProvider Services { get; } = new ServiceCollection().BuildServiceProvider();

        public ICockpitActions Actions { get; } = new NoActions();

        public IPluginStorage Storage { get; } = new MemoryStorage();

        public void AddSettings(Func<Control> createView) => SettingsRegistered++;

        public void AddSideMenuButton(string title, Action onInvoke) => SideButtons.Add(title);

        public void AddSideMenuSection(string title, Func<Control> createView) => SideSections.Add(title);

        public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) => Task.CompletedTask;
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
