using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;

namespace Cockpit.Infrastructure.Plugins;

// AC-1013: installs plugins that ship *with* the app (ex-core features, kept toggleable). Copied in and
// pre-approved only on first appearance (per-id seed ledger); afterward it's an ordinary store-managed
// plugin — never rolled back, re-pinned, re-seeded, or overriding an operator's disable.
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

        // Classify by prior appearance: already-seeded ids are the store's to manage now; not-yet-seeded but
        // already-installed ids (pre-dating this ledger, or operator-installed) are adopted — seed recorded,
        // bytes untouched. Only a genuinely absent source is copied in fresh.
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
