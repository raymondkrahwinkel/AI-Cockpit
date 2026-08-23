using System.Reflection;
using System.Runtime.Loader;

namespace Cockpit.App.Plugins;

// Loads one plugin's assemblies in isolation (MS "app with plugins" pattern): resolves the plugin's own
// dependencies from its folder, falling through to the default (host) context for anything shared
// (Avalonia, Cockpit.Plugins.Abstractions), so shared types keep a single identity. Non-collectible.
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
