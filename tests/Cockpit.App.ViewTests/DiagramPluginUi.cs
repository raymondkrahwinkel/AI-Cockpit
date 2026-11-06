using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.ViewTests;

// The second half of loading Diagram as PluginManager/PluginUiManager do — InitializeUi on a UI host sharing the
// test host's channel hub. F2.13/AC-1401: activates the UI part via PluginUiManager.ActivateUi like production does,
// instead of casting the backend plugin to ICockpitPluginUi — no longer possible once the parts physically split.
internal static class DiagramPluginUi
{
    public const string PluginId = "diagram";

    private static readonly Dictionary<string, string> _installed = [];

    // AC-2025: the plugin loads from a copy of an installed folder that is hashed against the pin, so the build output
    // is installed under a temp `plugins/diagram` once per process and pinned with the closure hash a real install gets.
    public static DiscoveredPlugin Discover(string buildOutput, PluginManifest manifest)
    {
        string folder;
        lock (_installed)
        {
            if (!_installed.TryGetValue(buildOutput, out folder!))
            {
                folder = Path.Combine(Path.GetTempPath(), "cockpit-viewtests-plugins", Guid.NewGuid().ToString("N"), "plugins", PluginId);
                foreach (var file in Directory.EnumerateFiles(buildOutput, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(folder, Path.GetRelativePath(buildOutput, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target);
                }

                _installed[buildOutput] = folder;
            }
        }

        var hash = PluginClosureHash.OfInstalledFolderAsync(folder).GetAwaiter().GetResult();
        return new DiscoveredPlugin(folder, PluginId, manifest, hash, PluginLoadDecision.Load);
    }

    public static void Initialize(DiscoveredPlugin discovered, ICockpitPlugin plugin, ICockpitHost host, PluginChannelHub hub, RecordingUiSurfaces surfaces)
    {
        var ui = PluginUiManager.ActivateUi(discovered, plugin) ?? throw new InvalidOperationException("Diagram's UI part did not activate.");
        var services = new ServiceCollection().AddSingleton(hub).AddSingleton<IWorkspaceTypeRegistry>(provider => new WorkspaceTypeRegistry(provider)).BuildServiceProvider();
        ui.InitializeUi(new CockpitUiHost(PluginId, "Diagram", host, services, surfaces, surfaces, surfaces, surfaces, []));
    }
}
