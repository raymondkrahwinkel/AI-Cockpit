using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;

namespace Cockpit.Infrastructure.Plugins;

/// <summary>
/// The shared "bring a plugins directory into line with a set of source folders" routine, used by both
/// <see cref="BundledPluginInstaller"/> (the plugins this build ships) and <see cref="DevPluginInstaller"/>
/// (a developer's freshly built first-party plugins, DEBUG only). Each source folder holds one plugin's
/// <c>plugin.json</c> and its files; the rule is the same for both callers, which is why it lives once here:
/// <list type="bullet">
/// <item>a plugin the operator disabled is left exactly as it is — shipping or rebuilding is not a reason to
/// reinstate a decision they made;</item>
/// <item>a version they updated past the source (from the store) is not rolled back;</item>
/// <item>a newer source version, or the same version built from different bytes (a rebuild has no version to
/// bump to), replaces the installed one and re-pins its consent to the new bytes.</item>
/// </list>
/// </summary>
internal sealed class PluginSourceInstaller(IPluginRegistrationStore registrations, ILogger? logger)
{
    /// <summary>
    /// Installs/refreshes each source folder into <paramref name="pluginsRoot"/> under the rule above.
    /// </summary>
    /// <param name="installNew">
    /// True to install a plugin that is not there yet (a bundled plugin ships, so it should simply be present);
    /// false to only refresh ones already installed (a dev sync must not silently install everything in the repo
    /// — the operator still chooses what their cockpit carries).
    /// </param>
    /// <returns>The ids installed or refreshed, for logging.</returns>
    public async Task<IReadOnlyList<string>> InstallFromSourceFoldersAsync(
        IEnumerable<string> sourceFolders,
        string pluginsRoot,
        bool installNew,
        CancellationToken cancellationToken = default)
    {
        var saved = await registrations.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        var installed = new List<string>();

        foreach (var source in sourceFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await _ReadManifestAsync(source, cancellationToken).ConfigureAwait(false) is not { } manifest)
            {
                continue;
            }

            var target = Path.Combine(pluginsRoot, manifest.Id);
            var savedRegistration = saved.GetValueOrDefault(manifest.Id);

            // A plugin the operator turned off stays off, and stays as it is on disk.
            if (savedRegistration is { Enabled: false })
            {
                continue;
            }

            // A dev sync refreshes what is installed; it does not decide, on the operator's behalf, to install
            // every first-party plugin in the repo just because it was built.
            if (!installNew && !Directory.Exists(target))
            {
                continue;
            }

            if (!await _NeedsInstallAsync(source, target, manifest, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            _CopyPlugin(source, target);

            // Pin the whole installed closure, not just the entry assembly (AC-43), so a later swapped dependency
            // DLL re-triggers consent.
            var sha = await PluginClosureHash.OfInstalledFolderAsync(target, cancellationToken).ConfigureAwait(false);
            await registrations.SaveAsync(manifest.Id, new PluginRegistration(Enabled: true, PinnedSha256: sha), cancellationToken).ConfigureAwait(false);

            installed.Add(manifest.Id);
        }

        return installed;
    }

    // Install when it is not there at all, when the source is newer than what is installed, or when it is the
    // same version built from different bytes. An installed version that is newer (the operator updated it from
    // the store) is left alone — a source build must not roll them back.
    private async Task<bool> _NeedsInstallAsync(string source, string target, PluginManifest incoming, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(target))
        {
            return true;
        }

        if (await _ReadManifestAsync(target, cancellationToken).ConfigureAwait(false) is not { } installed)
        {
            return true;
        }

        if (PluginVersion.IsNewer(incoming.Version, installed.Version))
        {
            return true;
        }

        if (PluginVersion.IsNewer(installed.Version, incoming.Version))
        {
            // The one skip worth a word. The rest are either nothing happening or the operator's own decision;
            // this one looks like the build did nothing, and the reason is a version they may have forgotten
            // updating past.
            logger?.LogInformation(
                "Plugin '{Plugin}' {IncomingVersion} is older than the {InstalledVersion} already installed, so it was left alone.",
                incoming.Id,
                incoming.Version,
                installed.Version);

            return false;
        }

        // Same version, which does not mean the same plugin: a rebuild never bumps one, because there is nothing
        // to bump it to. The version was standing in for "is this different" and answering for the wrong thing —
        // so ask what the question actually meant. "Different" now spans the whole closure, not just the entry
        // assembly (AC-43): a rebuild that only changed a dependency DLL must still be re-installed and re-pinned,
        // or discovery would later find a closure that no longer matches the pin and drop it to needs-consent.
        return !await _IsSameClosureAsync(source, target, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> _IsSameClosureAsync(string source, string target, CancellationToken cancellationToken)
    {
        // The source hashed as it would be installed (same file selection as _CopyPlugin), against what is on disk.
        var incoming = await PluginClosureHash.OfSourceFolderAsync(source, cancellationToken).ConfigureAwait(false);
        var installed = await PluginClosureHash.OfInstalledFolderAsync(target, cancellationToken).ConfigureAwait(false);

        return string.Equals(incoming, installed, StringComparison.OrdinalIgnoreCase);
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
            var name = Path.GetFileName(file);

            // One predicate decides what counts as a copied plugin file, shared with the closure hash that later
            // verifies the install (AC-43) so the two cannot drift. It leaves out the shared abstractions assembly
            // — a copy in a plugin folder gives that plugin's ICockpitPlugin a second identity and the loader then
            // rejects it as having "no usable entry type"; a plugin references it Private=false and never carries
            // it — and reserved dot-prefixed markers.
            if (!PluginClosureHash.IsCopiedSourceFile(name))
            {
                continue;
            }

            File.Copy(file, Path.Combine(target, name), overwrite: true);
        }
    }
}
