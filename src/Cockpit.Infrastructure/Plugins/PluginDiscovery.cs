using Cockpit.Core.Abstractions;
using Cockpit.Core.Plugins;

namespace Cockpit.Infrastructure.Plugins;

// Scans the plugins root for plugin subfolders, parses each `plugin.json`, hashes its load closure and
// runs the pure `PluginLoadPolicy` to decide what to do. Pure discovery — loads no assemblies, the loader
// acts on the results. A folder with a missing/invalid manifest or entry assembly is skipped silently.
internal sealed class PluginDiscovery : ISingletonService
{
    public async Task<IReadOnlyList<DiscoveredPlugin>> DiscoverAsync(
        string pluginsRoot,
        IReadOnlyDictionary<string, PluginRegistration> saved,
        int hostAbstractionsMajor,
        CancellationToken cancellationToken = default)
    {
        var result = new List<DiscoveredPlugin>();
        if (!Directory.Exists(pluginsRoot))
        {
            return result;
        }

        foreach (var folder in Directory.EnumerateDirectories(pluginsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip reserved, dot-prefixed folders (a leftover .staging-* extraction or the .pending-updates
            // staging area): they hold a valid manifest but are not installed plugins, so discovering them
            // would surface a phantom duplicate.
            if (Path.GetFileName(folder).StartsWith('.'))
            {
                continue;
            }

            // Marked for removal: treated as gone even though the folder survives until next start deletes it —
            // this keeps a removed plugin from reloading if that deletion ever fails (e.g. a locked file).
            if (File.Exists(Path.Combine(folder, PluginInstaller.RemovalMarker)))
            {
                continue;
            }

            var manifestPath = Path.Combine(folder, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            if (!PluginManifest.TryParse(json, out var manifest, out _) || manifest is null)
            {
                continue;
            }

            var entryPath = Path.Combine(folder, manifest.EntryAssembly);
            if (!File.Exists(entryPath))
            {
                continue;
            }

            // Hash the whole load closure, not just the entry assembly (AC-43): a swapped dependency DLL must
            // re-trigger consent too, since the loader runs it in-process with full trust.
            var hash = await PluginClosureHash.OfInstalledFolderAsync(folder, cancellationToken).ConfigureAwait(false);
            var folderId = Path.GetFileName(folder);
            saved.TryGetValue(folderId, out var registration);
            var decision = PluginLoadPolicy.Decide(manifest, hostAbstractionsMajor, registration, hash, HostVersionInfo.Current);

            result.Add(new DiscoveredPlugin(folder, folderId, manifest, hash, decision));
        }

        return result;
    }
}
