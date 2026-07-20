using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;

namespace Cockpit.Infrastructure.Plugins;

/// <summary>
/// Installs the plugins that ship <em>with</em> the app: the ones that used to be core features and were moved
/// out to a plugin (transcript search, git status). Shipping them means an operator has them out of the box
/// rather than having to know they exist — while the code stays a plugin, so it can be turned off and does not
/// drag one provider's file format back into a multi-provider core.
///
/// <para>
/// A bundled plugin is an ordinary, store-updatable plugin that merely comes pre-seeded. It is copied from the
/// app's <c>bundled-plugins/</c> folder into the operator's plugins directory on its <em>first appearance</em>
/// only, and pre-approved (the consent dialog asks "do you trust this third-party code"; these came out of the
/// very build that is asking). After that first seed the bundle never touches it again: later versions arrive
/// through the store like any other plugin's, and a version the operator installed is neither rolled back nor
/// re-pinned by a new app build. The seed is recorded per id in <see cref="Cockpit.Infrastructure.Configuration.CockpitConfigFile.SeededBundledPlugins"/>,
/// so it survives both an uninstall (a plugin the operator removed does not silently return next start) and a
/// store update (a newer installed version is left where it is).
/// </para>
///
/// It never overrides the operator: a plugin they disabled stays disabled and is left untouched on disk.
/// </summary>
public sealed class BundledPluginInstaller : ISingletonService
{
    /// <summary>Folder in the app's output holding one subfolder per bundled plugin (its dll, deps.json and plugin.json).</summary>
    public const string BundledFolderName = "bundled-plugins";

    private readonly IPluginRegistrationStore _registrations;
    private readonly ILogger<BundledPluginInstaller>? _logger;

    /// <param name="logger">Optional: this also runs before the container exists, and a skipped plugin is not worth failing to start over.</param>
    public BundledPluginInstaller(ILogger<BundledPluginInstaller>? logger = null)
        : this(new PluginRegistrationStore(), logger)
    {
    }

    /// <summary>Test seam: install against an in-memory registration store instead of <c>cockpit.json</c>.</summary>
    internal BundledPluginInstaller(IPluginRegistrationStore registrations, ILogger<BundledPluginInstaller>? logger = null)
    {
        _registrations = registrations;
        _logger = logger;
    }

    /// <summary>
    /// Brings the operator's plugins directory up to date with what this build ships.
    /// </summary>
    /// <param name="bundledRoot">The app's <c>bundled-plugins/</c> folder; nothing happens when it does not exist.</param>
    /// <param name="pluginsRoot">The operator's plugins directory (next to <c>cockpit.json</c>).</param>
    /// <returns>The ids of the plugins installed or updated, for logging. Empty when everything was already current.</returns>
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

        // Classify each bundled source by whether it has appeared before. An id already in the seed ledger is the
        // store's to manage now — the bundle never touches it again. An id not yet seeded but already installed
        // (a folder or a saved registration) is an existing install from before this ledger existed, or one the
        // operator installed themselves: adopt it — record the seed, but do not overwrite its bytes. Only a source
        // that is genuinely absent is copied in fresh.
        var freshSeedSources = new List<string>();
        var toRecord = new List<string>();
        foreach (var source in Directory.EnumerateDirectories(bundledRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await _ReadManifestIdAsync(source, cancellationToken).ConfigureAwait(false) is not { } id
                || seeded.Contains(id))
            {
                continue;
            }

            toRecord.Add(id);
            var alreadyInstalled = saved.ContainsKey(id) || Directory.Exists(Path.Combine(pluginsRoot, id));
            if (!alreadyInstalled)
            {
                freshSeedSources.Add(source);
            }
        }

        // The genuinely-absent sources are the only ones copied. Their targets do not exist, so the shared
        // installer's replace/re-pin rules never fire — this is a clean first install and nothing else.
        var installed = freshSeedSources.Count == 0
            ? (IReadOnlyList<string>)[]
            : await new PluginSourceInstaller(_registrations, _logger)
                .InstallFromSourceFoldersAsync(freshSeedSources, pluginsRoot, installNew: true, cancellationToken)
                .ConfigureAwait(false);

        // Record every classified id — freshly seeded and adopted alike — so none is ever seeded again.
        await _registrations.MarkBundledSeededAsync(toRecord, cancellationToken).ConfigureAwait(false);

        return installed;
    }

    private static async Task<string?> _ReadManifestIdAsync(string folder, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(folder, "plugin.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        return PluginManifest.TryParse(json, out var manifest, out _) && manifest is not null ? manifest.Id : null;
    }
}
