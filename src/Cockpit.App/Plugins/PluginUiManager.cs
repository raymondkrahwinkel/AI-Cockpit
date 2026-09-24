using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.App.Plugins;

// AC-1392: the desktop's half of the plugin lifecycle (AC-1389's phase 3), split off PluginManager so the backend
// half names no window type. Runs after every backend part's Initialize, over what PluginManager loaded.
public sealed class PluginUiManager(ILogger<PluginUiManager> logger, PluginDiagnostics diagnostics, PluginManager backend) : IDisposable
{
    private readonly Dictionary<DiscoveredPlugin, ICockpitPluginUi> _ui = [];

    // AC-1033: the assembly each loaded plugin came out of, which is where the knowledge base reads its embedded
    // documentation. AC-1389: a plugin that is only a UI part has an assembly once InitializeUi activated it.
    public IReadOnlyList<(DiscoveredPlugin Discovered, Assembly Assembly)> LoadedWithAssemblies =>
        [.. _PartAssemblies()];

    private IEnumerable<(DiscoveredPlugin Discovered, Assembly Assembly)> _PartAssemblies()
    {
        foreach (var (discovered, plugin) in backend.LoadedParts)
        {
            if (((object?)plugin ?? _ui.GetValueOrDefault(discovered)) is { } part)
            {
                yield return (discovered, part.GetType().Assembly);
            }
        }
    }

    // Activate each plugin's UI part and hand it the host `hostFor` built. A UI part that cannot load or throws is
    // recorded with its reason; the backend part stays loaded either way.
    public void InitializeUi(
        Func<DiscoveredPlugin, ICockpitPlugin?, ICockpitPluginUi?> activateUi,
        Func<DiscoveredPlugin, ICockpitPluginUi, ICockpitUiHost> hostFor)
    {
        foreach (var (discovered, plugin) in backend.LoadedParts.Where(entry => !backend.InitializeFailed(entry.Discovered)))
        {
            try
            {
                if (activateUi(discovered, plugin) is not { } ui)
                {
                    continue;
                }

                _ui[discovered] = ui;
                ui.InitializeUi(hostFor(discovered, ui));
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Plugin {PluginId}'s UI part failed; its UI contributions are skipped.", discovered.FolderId);
                diagnostics.Record(discovered.FolderId, discovered.Manifest.Name, "initialize-ui", exception.Message);
            }
        }
    }

    // AC-1389: the plugin's UI part, or null when it has none. Without a uiAssembly that is the backend entry type
    // itself when it implements ICockpitPluginUi too (a plugin moving its UI over before it splits); with one, the
    // UI assembly loads in the backend part's own context. Throws with the reason when the UI part is unusable.
    public static ICockpitPluginUi? ActivateUi(DiscoveredPlugin discovered, ICockpitPlugin? backend)
    {
        var manifest = discovered.Manifest;
        if (manifest.UiAssembly is not { } uiAssembly)
        {
            return backend as ICockpitPluginUi;
        }

        if (!PluginEntryPath.TryResolve(discovered.FolderPath, uiAssembly, out var uiPath))
        {
            throw new InvalidOperationException($"The UI assembly path '{uiAssembly}' is outside the plugin's folder.");
        }

        var assembly = (backend is null ? null : AssemblyLoadContext.GetLoadContext(backend.GetType().Assembly)) switch
        {
            PluginLoadContext shared => shared.LoadAlongside(uiPath),
            { } other => other.LoadFromAssemblyPath(uiPath),
            null => new PluginLoadContext(uiPath).LoadFromAssemblyPath(uiPath),
        };

        var uiType = _ResolveUiEntryType(assembly, manifest.UiEntryType)
            ?? throw new InvalidOperationException(
                $"The UI entry type {manifest.UiEntryType ?? "(one ICockpitPluginUi implementation)"} is not a concrete {nameof(ICockpitPluginUi)} in '{uiAssembly}'.");

        return (ICockpitPluginUi?)Activator.CreateInstance(uiType);
    }

    private static Type? _ResolveUiEntryType(Assembly assembly, string? uiEntryTypeName)
    {
        if (!string.IsNullOrWhiteSpace(uiEntryTypeName))
        {
            var named = assembly.GetType(uiEntryTypeName, throwOnError: false);
            return named is not null && _IsConcreteUi(named) ? named : null;
        }

        var candidates = assembly.GetTypes().Where(_IsConcreteUi).ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static bool _IsConcreteUi(Type type) =>
        typeof(ICockpitPluginUi).IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false };

    // A UI part that is its own object, not the backend entry type doing both, goes here; the backend part is
    // PluginManager's to dispose.
    public void Dispose()
    {
        foreach (var (discovered, plugin) in backend.LoadedParts)
        {
            if (_ui.GetValueOrDefault(discovered) is not IDisposable ui || ReferenceEquals(ui, plugin))
            {
                continue;
            }

            try
            {
                ui.Dispose();
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Plugin {PluginId} threw while disposing.", discovered.FolderId);
            }
        }

        _ui.Clear();
    }
}
