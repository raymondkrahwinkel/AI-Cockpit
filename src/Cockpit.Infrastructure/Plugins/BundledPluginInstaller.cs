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
/// They are copied from the app's <c>bundled-plugins/</c> folder into the operator's plugins directory on
/// startup, and pre-approved: the consent dialog exists to ask "do you trust this third-party code", and these
/// came out of the very build that is asking. A newer bundled version replaces an older installed one, keeping
/// the plugin enabled and its settings.
///
/// It never overrides the operator: a plugin they disabled stays disabled and is left untouched on disk.
/// </summary>
public sealed class BundledPluginInstaller : ISingletonService
{
    /// <summary>Folder in the app's output holding one subfolder per bundled plugin (its dll, deps.json and plugin.json).</summary>
    public const string BundledFolderName = "bundled-plugins";

    private readonly IPluginRegistrationStore _registrations;

    public BundledPluginInstaller()
        : this(new PluginRegistrationStore())
    {
    }

    /// <summary>Test seam: install against an in-memory registration store instead of <c>cockpit.json</c>.</summary>
    internal BundledPluginInstaller(IPluginRegistrationStore registrations)
    {
        _registrations = registrations;
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
            return [];
        }

        var saved = await _registrations.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        var installed = new List<string>();

        foreach (var source in Directory.EnumerateDirectories(bundledRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await _ReadManifestAsync(source, cancellationToken).ConfigureAwait(false) is not { } manifest)
            {
                continue;
            }

            var target = Path.Combine(pluginsRoot, manifest.Id);
            var savedRegistration = saved.GetValueOrDefault(manifest.Id);

            // A plugin the operator turned off stays off, and stays as it is on disk: shipping a new build is
            // not a reason to reinstate something they deliberately removed from their cockpit.
            if (savedRegistration is { Enabled: false })
            {
                continue;
            }

            if (!_NeedsInstall(source, target, manifest, cancellationToken))
            {
                continue;
            }

            _CopyPlugin(source, target);

            var entryAssembly = Path.Combine(target, manifest.EntryAssembly);
            var sha = PluginHash.Compute(await File.ReadAllBytesAsync(entryAssembly, cancellationToken).ConfigureAwait(false));
            await _registrations.SaveAsync(manifest.Id, new PluginRegistration(Enabled: true, PinnedSha256: sha), cancellationToken).ConfigureAwait(false);

            installed.Add(manifest.Id);
        }

        return installed;
    }

    // Install when it is not there at all, or when what we ship is newer than what is installed. An installed
    // version that is newer (the operator updated it from the store) is left alone — shipping a build must not
    // roll them back.
    private bool _NeedsInstall(string source, string target, PluginManifest bundled, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(target))
        {
            return true;
        }

        var installed = _ReadManifestAsync(target, cancellationToken).GetAwaiter().GetResult();
        return installed is null || PluginVersion.IsNewer(bundled.Version, installed.Version);
    }

    private static async Task<PluginManifest?> _ReadManifestAsync(string folder, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(folder, "plugin.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        return PluginManifest.TryParse(json, out var manifest, out _) ? manifest : null;
    }

    // Replaces the plugin's files wholesale rather than merging: a leftover assembly from an older version is
    // exactly the kind of thing that loads and then fails halfway.
    private static void _CopyPlugin(string source, string target)
    {
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
    }
}
