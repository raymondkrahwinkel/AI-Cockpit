using System.Reflection;
using System.Runtime.Loader;

namespace Cockpit.Infrastructure.Plugins;

// Resolves one plugin's dependencies separately (MS "app with plugins" pattern): its own from its folder,
// shared ones (Avalonia, Cockpit.Plugins.Abstractions) from the host context, so shared types keep one
// identity. Non-collectible. AC-479: dependency isolation, NOT a security boundary — see PLUGIN-SDK.md.

// AC-1391: public — the desktop's PluginUiManager loads a UI part alongside the backend part across the boundary.
// AC-1403: `refuseUiFor` names the plugin in a backend without a frontend, where Avalonia is refused with that reason.
// AC-2025: with a `shadow` the context loads only files it has just hashed to what was approved.
public sealed class PluginLoadContext(string pluginMainAssemblyPath, string? refuseUiFor = null, PluginShadow? shadow = null) : AssemblyLoadContext
{
    private readonly List<AssemblyDependencyResolver> _resolvers = [new(pluginMainAssemblyPath)];

    // AC-1389: a UI part loaded into its backend part's context brings its own deps.json, read after the backend's.
    public Assembly LoadAlongside(string assemblyPath)
    {
        if (_Loaded(assemblyPath) is { } loaded)
        {
            return loaded;
        }

        _resolvers.Add(new AssemblyDependencyResolver(assemblyPath));
        return _LoadVerified(assemblyPath);
    }

    // AC-2024: a plugin enabled again comes back into the context it left, which holds its assembly until the restart.
    public Assembly LoadOnce(string assemblyPath) => _Loaded(assemblyPath) ?? _LoadVerified(assemblyPath);

    // Hashed right before it is handed to the runtime; a file outside the approved closure, or one that changed since, is refused.
    private Assembly _LoadVerified(string path)
    {
        shadow?.Verify(path);
        return LoadFromAssemblyPath(path);
    }

    private Assembly? _Loaded(string assemblyPath) =>
        Assemblies.FirstOrDefault(assembly => string.Equals(assembly.Location, assemblyPath, StringComparison.Ordinal));

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (refuseUiFor is not null && assemblyName.Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true)
        {
            throw new InvalidOperationException(
                $"The backend part of plugin {refuseUiFor} touches {assemblyName.Name}; a backend has no UI.");
        }

        var path = _resolvers.Select(resolver => resolver.ResolveAssemblyToPath(assemblyName)).FirstOrDefault(found => found is not null);
        return path is null ? null : _LoadVerified(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolvers.Select(resolver => resolver.ResolveUnmanagedDllToPath(unmanagedDllName)).FirstOrDefault(found => found is not null);
        if (path is null)
        {
            return nint.Zero;
        }

        shadow?.Verify(path);
        return LoadUnmanagedDllFromPath(path);
    }
}
