using System.Reflection;
using System.Runtime.Loader;

namespace Cockpit.App.Plugins;

// Resolves one plugin's dependencies separately (MS "app with plugins" pattern): its own from its folder,
// shared ones (Avalonia, Cockpit.Plugins.Abstractions) from the host context, so shared types keep one
// identity. Non-collectible. AC-479: dependency isolation, NOT a security boundary — see PLUGIN-SDK.md.
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
