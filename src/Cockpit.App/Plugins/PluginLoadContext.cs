using System.Reflection;
using System.Runtime.Loader;

namespace Cockpit.App.Plugins;

/// <summary>
/// Loads one plugin's assemblies in isolation (the MS "app with plugins" pattern): the
/// <see cref="AssemblyDependencyResolver"/> resolves the plugin's own dependencies from its folder, while
/// anything the plugin does not carry — Avalonia, CommunityToolkit, Cockpit.Plugins.Abstractions — falls
/// through to the default (host) context, so shared types keep a single identity across the boundary.
/// Non-collectible: a loaded UI plugin cannot be truly unloaded (disable = UI off + Dispose; memory frees
/// on restart).
/// </summary>
internal sealed class PluginLoadContext(string pluginMainAssemblyPath) : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver = new(pluginMainAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}
