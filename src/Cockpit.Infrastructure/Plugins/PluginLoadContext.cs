using System.Reflection;
using System.Runtime.Loader;

namespace Cockpit.Infrastructure.Plugins;

// Resolves one plugin's dependencies separately (MS "app with plugins" pattern): its own from its folder,
// shared ones (Avalonia, Cockpit.Plugins.Abstractions) from the host context, so shared types keep one
// identity. Non-collectible. AC-479: dependency isolation, NOT a security boundary — see PLUGIN-SDK.md.

// AC-1391: public — the desktop's PluginUiManager loads a UI part alongside the backend part across the boundary.
// AC-1403: `refuseUiFor` names the plugin in a backend without a frontend, where resolving Avalonia is refused with
// that reason instead of a bare FileNotFoundException whenever the JIT first meets an Avalonia type.
public sealed class PluginLoadContext(string pluginMainAssemblyPath, string? refuseUiFor = null) : AssemblyLoadContext
{
    private readonly List<AssemblyDependencyResolver> _resolvers = [new(pluginMainAssemblyPath)];

    // AC-1389: a UI part loaded into its backend part's context brings its own deps.json, read after the backend's.
    public Assembly LoadAlongside(string assemblyPath)
    {
        _resolvers.Add(new AssemblyDependencyResolver(assemblyPath));
        return LoadFromAssemblyPath(assemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (refuseUiFor is not null && assemblyName.Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true)
        {
            throw new InvalidOperationException(
                $"The backend part of plugin {refuseUiFor} touches {assemblyName.Name}; a backend has no UI.");
        }

        var path = _resolvers.Select(resolver => resolver.ResolveAssemblyToPath(assemblyName)).FirstOrDefault(found => found is not null);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolvers.Select(resolver => resolver.ResolveUnmanagedDllToPath(unmanagedDllName)).FirstOrDefault(found => found is not null);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}
