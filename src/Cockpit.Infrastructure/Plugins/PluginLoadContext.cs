using System.Reflection;
using System.Runtime.Loader;

namespace Cockpit.Infrastructure.Plugins;

// Resolves one plugin's dependencies separately (MS "app with plugins" pattern): its own from its folder,
// shared ones (Avalonia, Cockpit.Plugins.Abstractions) from the host context, so shared types keep one
// identity. Non-collectible. AC-479: dependency isolation, NOT a security boundary — see PLUGIN-SDK.md.

// AC-1391: public — PluginActivator (Cockpit.App) still constructs and calls it directly across the boundary.
public sealed class PluginLoadContext(string pluginMainAssemblyPath) : AssemblyLoadContext
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
        var path = _resolvers.Select(resolver => resolver.ResolveAssemblyToPath(assemblyName)).FirstOrDefault(found => found is not null);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolvers.Select(resolver => resolver.ResolveUnmanagedDllToPath(unmanagedDllName)).FirstOrDefault(found => found is not null);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}
