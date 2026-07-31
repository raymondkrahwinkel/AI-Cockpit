using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.App.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

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
        Assert.NotNull(folder);

        var manifestJson = File.ReadAllText(Path.Combine(folder!, "plugin.json"));
        Assert.True(PluginManifest.TryParse(manifestJson, out var manifest, out _));
        Assert.NotNull(manifest);

        var hash = PluginHash.Compute(File.ReadAllBytes(Path.Combine(folder, manifest!.EntryAssembly)));
        var discovered = new DiscoveredPlugin(folder, "claude-provider", manifest, hash, PluginLoadDecision.Load);

        var activator = new PluginActivator(NullLogger<PluginActivator>.Instance);
        var plugin = activator.Activate(discovered);

        Assert.NotNull(plugin);
        Assert.Equal("claude-provider", plugin!.Metadata.Id);
        Assert.Equal("Claude Code", plugin.Metadata.DisplayName);

        plugin.ConfigureServices(new ServiceCollection());

        var host = new RecordingHost();
        plugin.Initialize(host);

        // Both routes register under the id the resolver routes a Claude profile to.
        Assert.Single(host.TtyProviders);
        var ttyRegistration = host.TtyProviders.Single();
        Assert.Equal("claude", ttyRegistration.ProviderId);
        Assert.Equal("Claude", ttyRegistration.DisplayName);
        Assert.Contains(ttyRegistration.Options, option => option.Key == "permission-mode");
        Assert.Contains(ttyRegistration.Options, option => option.Key == "model");
        Assert.Contains(ttyRegistration.Options, option => option.Key == "effort");
        Assert.NotNull(ttyRegistration.CreateProvider(host.Services));

        // The SDK/session-driver route (weg A): control-protocol permissions, so it reports SupportsPermissions and
        // mints a driver factory through the real activator.
        Assert.Single(host.SessionProviders);
        var sessionRegistration = host.SessionProviders.Single();
        Assert.Equal("claude", sessionRegistration.ProviderId);
        Assert.Equal("Claude", sessionRegistration.DisplayName);
        Assert.True(sessionRegistration.Capabilities.SupportsPermissions);
        // Vision rides the registration capabilities, which is the object the host honours: SessionDriverFactory
        // builds the driver adapter from registration.Capabilities, not the driver instance's own. Regression guard
        // for the pasted image being gated off ("provider does not support image input") when this was left false.
        Assert.True(sessionRegistration.Capabilities.SupportsVision);
        // AC-190: Claude confines to the worktree (AC-174) but only while its permission system is engaged, so it must
        // declare BOTH — the confinement vouch and that it is permission-based — for the adapter to downgrade a bypass
        // session to unconfined and the fail-closed isolation gate to refuse it.
        Assert.True(sessionRegistration.Capabilities.ConfinesFileAccessToWorkingDirectory);
        Assert.True(sessionRegistration.Capabilities.ConfinesViaPermissionsOnly);
        Assert.Contains(sessionRegistration.Options, option => option.Key == "permission-mode");
        Assert.Contains(sessionRegistration.Options, option => option.Key == "model");
        Assert.NotNull(sessionRegistration.CreateDriverFactory(host.Services));
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
