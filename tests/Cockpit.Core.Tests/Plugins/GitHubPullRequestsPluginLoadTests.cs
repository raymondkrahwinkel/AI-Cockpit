using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.App.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// End-to-end loader proof (#41), mirroring <see cref="GitHubIssuesPluginLoadTests"/>: loads the real
/// compiled GitHub Pull Requests plugin — both its parts since AC-1396 — through the actual <see cref="PluginActivator"/> /
/// <see cref="PluginLoadContext"/> and asserts type-identity holds (the plugin's ICockpitPlugin resolves to
/// the host's copy — the cast would be null otherwise), its metadata is right, and its Options-tab + badged
/// side-menu-button contributions register (AC-517 — no plain side-menu section any more, the badged button
/// is the "view all" entry point). The test project builds the plugin (a ReferenceOutputAssembly=false
/// project reference), so its output is always present.
/// </summary>
public class GitHubPullRequestsPluginLoadTests
{
    [Fact]
    public void ActivatesAndContributes_WhenBuilt()
    {
        var folder = _LocatePluginOutput();
        Assert.NotNull(folder);

        var manifestJson = File.ReadAllText(Path.Combine(folder!, "plugin.json"));
        Assert.True(PluginManifest.TryParse(manifestJson, out var manifest, out _));
        Assert.NotNull(manifest);

        // AC-1396: the entry (backend) assembly specifically — manifest.Assemblies now also yields UiAssembly.
        var hash = PluginHash.Compute(File.ReadAllBytes(Path.Combine(folder, manifest!.EntryAssembly!)));
        var discovered = new DiscoveredPlugin(folder, "github-pull-requests", manifest, hash, PluginLoadDecision.Load);

        var activator = new PluginActivator(NullLogger<PluginActivator>.Instance);
        var plugin = activator.Activate(discovered);

        // A non-null cast to the host's ICockpitPlugin is itself the type-identity proof.
        Assert.NotNull(plugin);
        Assert.Equal("github-pull-requests", plugin!.Metadata.Id);
        Assert.Equal("GitHub Pull Requests", plugin.Metadata.DisplayName);

        plugin.ConfigureServices(new ServiceCollection());

        var host = new RecordingHost();
        plugin.Initialize(host);

        // AC-1396: the backend part contributes nothing with a window; the settings view and the badged button
        // come from the UI part.
        Assert.Equal(0, host.SettingsRegistered);
        Assert.Empty(host.SideButtons);
        Assert.Empty(host.SideSections);
        Assert.Empty(host.BadgedSideButtons);

        Assert.Equal("Cockpit.Plugin.GitHubPullRequests.UI.dll", manifest.UiAssembly);
        Assert.Equal("Cockpit.Plugin.GitHubPullRequests.UI.GitHubPullRequestsUi", manifest.UiEntryType);
        var uiPart = Assert.IsAssignableFrom<ICockpitPluginUi>(PluginUiManager.ActivateUi(discovered, plugin));
        var uiHost = Substitute.For<ICockpitUiHost>();
        uiHost.AddSideMenuButtonWithBadge(Arg.Any<string>(), Arg.Any<Action>()).Returns(new SideMenuButtonBadge());
        uiPart.InitializeUi(uiHost);
        uiHost.Received(1).AddSettings(Arg.Any<Func<Control>>());
        uiHost.Received(1).AddSideMenuButtonWithBadge("Open PRs", Arg.Any<Action>());
        uiHost.DidNotReceive().AddSideMenuButton(Arg.Any<string>(), Arg.Any<Action>());

        plugin.Dispose();
    }

    // Walks up from the test output to the repo root and finds the plugin's build output (either config).
    private static string? _LocatePluginOutput()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidateRoot = Path.Combine(directory.FullName, "plugins-dev", "Cockpit.Plugin.GitHubPullRequests", "bin");
            if (Directory.Exists(candidateRoot))
            {
                var dll = Directory
                    .EnumerateFiles(candidateRoot, "Cockpit.Plugin.GitHubPullRequests.dll", SearchOption.AllDirectories)
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

        public List<string> BadgedSideButtons { get; } = [];

        public IServiceProvider Services { get; } = new ServiceCollection().BuildServiceProvider();

        public ICockpitActions Actions { get; } = new NoActions();

        public IPluginStorage Storage { get; } = new MemoryStorage();

        public void AddSettings(Func<Control> createView) => SettingsRegistered++;

        public void AddSideMenuButton(string title, Action onInvoke) => SideButtons.Add(title);

        public void AddSideMenuSection(string title, Func<Control> createView) => SideSections.Add(title);

        public SideMenuButtonBadge AddSideMenuButtonWithBadge(string title, Action onInvoke)
        {
            BadgedSideButtons.Add(title);
            return new SideMenuButtonBadge();
        }

        public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) => Task.CompletedTask;

        public Task ShowDialogAsync(string title, Func<Control> createContent, string singleInstanceKey, double width = 720, double height = 560) =>
            Task.CompletedTask;
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
