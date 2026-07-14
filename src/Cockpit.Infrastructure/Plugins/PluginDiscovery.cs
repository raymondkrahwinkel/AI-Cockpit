using Cockpit.Core.Abstractions;
using Cockpit.Core.Plugins;

namespace Cockpit.Infrastructure.Plugins;

/// <summary>
/// Scans the plugins root (a <c>plugins/</c> folder next to <c>cockpit.json</c>) for plugin subfolders,
/// parses each <c>plugin.json</c>, hashes its entry assembly and runs the pure <see cref="PluginLoadPolicy"/>
/// to decide what should happen with it. Pure discovery — it loads no assemblies; the loader acts on the
/// results. A folder with a missing/invalid manifest or a missing entry assembly is skipped silently
/// (it is not a valid plugin).
/// </summary>
internal sealed class PluginDiscovery : ISingletonService
{
    /// <summary>
    /// This cockpit's version, which a plugin's <c>minHostVersion</c> is measured against. Read from the running
    /// assembly rather than a constant, so it cannot drift from what the app actually is.
    /// </summary>
    private static Version HostVersion { get; } =
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);

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

            var hash = PluginHash.Compute(await File.ReadAllBytesAsync(entryPath, cancellationToken).ConfigureAwait(false));
            var folderId = Path.GetFileName(folder);
            saved.TryGetValue(folderId, out var registration);
            var decision = PluginLoadPolicy.Decide(manifest, hostAbstractionsMajor, registration, hash, HostVersion);

            result.Add(new DiscoveredPlugin(folder, folderId, manifest, hash, decision));
        }

        return result;
    }
}
