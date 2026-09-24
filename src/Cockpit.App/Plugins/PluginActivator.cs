using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.App.Plugins;

// #14: turns a `DiscoveredPlugin` into a live `ICockpitPlugin` by loading it in its own
// `PluginLoadContext` and instantiating the entry type; `PluginManager.LoadAndConfigure` isolates any throw here.
internal sealed class PluginActivator(ILogger<PluginActivator> logger)
{
    public ICockpitPlugin? Activate(DiscoveredPlugin discovered)
    {
        // AC-1159: discovery already checked this, but the activator loads in-process with full trust and
        // must not trust that a manifest read back off disk still resolves the same way.
        if (discovered.Manifest.EntryAssembly is not { } entryAssembly
            || !PluginEntryPath.TryResolve(discovered.FolderPath, entryAssembly, out var entryPath))
        {
            logger.LogWarning(
                "Plugin {PluginId} has an entry assembly path outside its folder ({EntryAssembly}); refusing to load it.",
                discovered.FolderId, discovered.Manifest.EntryAssembly);
            return null;
        }

        var context = new PluginLoadContext(entryPath);
        var assembly = context.LoadFromAssemblyPath(entryPath);

        var entryType = _ResolveEntryType(assembly, discovered.Manifest.EntryType);
        if (entryType is null)
        {
            logger.LogWarning(
                "Plugin {PluginId} has no usable entry type (looked for {EntryType}); skipping it.",
                discovered.FolderId, discovered.Manifest.EntryType ?? "an ICockpitPlugin implementation");
            return null;
        }

        return Activator.CreateInstance(entryType) as ICockpitPlugin;
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

    // The manifest may name the entry type explicitly; otherwise the assembly must carry exactly one
    // concrete ICockpitPlugin — an ambiguous or empty assembly is rejected rather than guessed.
    private static Type? _ResolveEntryType(Assembly assembly, string? entryTypeName)
    {
        if (!string.IsNullOrWhiteSpace(entryTypeName))
        {
            var named = assembly.GetType(entryTypeName, throwOnError: false);
            return named is not null && _IsConcretePlugin(named) ? named : null;
        }

        var candidates = assembly.GetTypes().Where(_IsConcretePlugin).ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static bool _IsConcretePlugin(Type type) =>
        typeof(ICockpitPlugin).IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false };
}
