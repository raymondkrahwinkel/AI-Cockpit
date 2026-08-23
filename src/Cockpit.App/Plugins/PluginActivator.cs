using System.Reflection;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Plugins;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

// #14: turns a `DiscoveredPlugin` into a live `ICockpitPlugin` by loading it in its own
// `PluginLoadContext` and instantiating the entry type; `PluginManager.LoadAndConfigure` isolates any throw here.
internal sealed class PluginActivator(ILogger<PluginActivator> logger)
{
    public ICockpitPlugin? Activate(DiscoveredPlugin discovered)
    {
        var entryPath = Path.Combine(discovered.FolderPath, discovered.Manifest.EntryAssembly);
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
