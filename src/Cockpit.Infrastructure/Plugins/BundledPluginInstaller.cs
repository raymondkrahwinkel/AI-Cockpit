using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Velopack;

namespace Cockpit.Infrastructure.Plugins;

// Seeds bundled plugins and upgrades unchanged seeds, preserving operator installs, pins and removals.
public sealed class BundledPluginInstaller : ISingletonService
{
    // Folder in the app's output holding one subfolder per bundled plugin (its dll, deps.json and plugin.json).
    public const string BundledFolderName = "bundled-plugins";

    private readonly IPluginRegistrationStore _registrations;
    private readonly ILogger<BundledPluginInstaller>? _logger;

    // `logger`: Optional: this also runs before the container exists, and a skipped plugin is not worth failing to start over.
    public BundledPluginInstaller(ILogger<BundledPluginInstaller>? logger = null)
        : this(new PluginRegistrationStore(), logger)
    {
    }

    // Test seam: install against an in-memory registration store instead of `cockpit.json`.
    internal BundledPluginInstaller(IPluginRegistrationStore registrations, ILogger<BundledPluginInstaller>? logger = null)
    {
        _registrations = registrations;
        _logger = logger;
    }

    // Brings the operator's plugins directory up to date with what this build ships. `bundledRoot`: the app's
    // `bundled-plugins/` folder, no-op if absent. `pluginsRoot`: operator's plugins directory. Returns ids
    // installed/updated, for logging; empty when already current.
    public async Task<IReadOnlyList<string>> InstallAsync(string bundledRoot, string pluginsRoot, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(bundledRoot))
        {
            // A single-file build has no folder beside it — it is one file, which is the point. The plugins ride
            // inside the executable instead, and are unpacked here on the way past.
            if (BundledPluginResources.TryExtract() is { } extracted)
            {
                bundledRoot = extracted;
            }
            else
            {
                return [];
            }
        }

        var seeded = await _registrations.LoadSeededBundledIdsAsync(cancellationToken).ConfigureAwait(false);
        var saved = await _registrations.LoadAllAsync(cancellationToken).ConfigureAwait(false);

        var sourcesToInstall = new List<string>();
        var toRecord = new List<string>();
        foreach (var source in Directory.EnumerateDirectories(bundledRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await _ReadManifestAsync(source, cancellationToken).ConfigureAwait(false) is not { } manifest)
            {
                continue;
            }

            var id = manifest.Id;
            if (saved.TryGetValue(id, out var registration)
                && registration.ExpectedMinVersion != manifest.ExpectedMinVersion)
            {
                // Seeded or not: the bundle's expectation moves with the build, not with the seed.
                await _registrations.SetExpectedMinVersionAsync(id, manifest.ExpectedMinVersion, cancellationToken).ConfigureAwait(false);
            }

            var target = Path.Combine(pluginsRoot, id);
            if (!Directory.Exists(target))
            {
                if (!seeded.Contains(id) && registration is null
                    && PluginLoadPolicy.MeetsMinHostVersion(manifest.MinHostVersion, HostVersionInfo.Current))
                {
                    sourcesToInstall.Add(source);
                }
                continue;
            }

            toRecord.Add(id);
            if (registration is { Enabled: false } or { VersionChosen: true } or { Source: not null }
                || !PluginInstaller.IsInstalledPlugin(target)
                || Directory.Exists(Path.Combine(pluginsRoot, ".pending-updates", id)))
            {
                continue;
            }

            // Outside the plugin folder: a manual ZIP replacement must not erase its provenance.
            var marker = Path.Combine(pluginsRoot, ".bundled-" + id);
            var hash = await PluginClosureHash.OfInstalledFolderAsync(target, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(marker))
            {
                await File.WriteAllTextAsync(marker, hash, cancellationToken).ConfigureAwait(false);
            }
            if (!string.Equals(await File.ReadAllTextAsync(marker, cancellationToken).ConfigureAwait(false), hash, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (await _ReadManifestAsync(target, cancellationToken).ConfigureAwait(false) is { } current
                && SemanticVersion.TryParse(manifest.Version, out var incomingVersion)
                && SemanticVersion.TryParse(current.Version, out var currentVersion)
                && incomingVersion.CompareTo(currentVersion) > 0
                && PluginLoadPolicy.MeetsMinHostVersion(manifest.MinHostVersion, HostVersionInfo.Current))
            {
                sourcesToInstall.Add(source);
            }
        }

        var installed = sourcesToInstall.Count == 0
            ? (IReadOnlyList<string>)[]
            : await new PluginSourceInstaller(_registrations, _logger)
                .InstallFromSourceFoldersAsync(sourcesToInstall, pluginsRoot, installNew: true, cancellationToken)
                .ConfigureAwait(false);

        foreach (var id in installed)
        {
            var hash = await PluginClosureHash.OfInstalledFolderAsync(Path.Combine(pluginsRoot, id), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(pluginsRoot, ".bundled-" + id), hash, cancellationToken).ConfigureAwait(false);
        }

        // Record every classified id — freshly seeded and adopted alike — so none is ever seeded again.
        await _registrations.MarkBundledSeededAsync(toRecord.Concat(installed), cancellationToken).ConfigureAwait(false);

        return installed;
    }

    private static async Task<PluginManifest?> _ReadManifestAsync(string folder, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(folder, "plugin.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        return PluginManifest.TryParse(json, out var manifest, out _) && manifest is not null ? manifest : null;
    }
}
