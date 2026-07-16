using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;

namespace Cockpit.Infrastructure.Plugins;

/// <summary>
/// Installs the plugins that ship <em>with</em> the app: the ones that used to be core features and were moved
/// out to a plugin (transcript search, git status). Shipping them means an operator has them out of the box
/// rather than having to know they exist — while the code stays a plugin, so it can be turned off and does not
/// drag one provider's file format back into a multi-provider core.
///
/// They are copied from the app's <c>bundled-plugins/</c> folder into the operator's plugins directory on
/// startup, and pre-approved: the consent dialog exists to ask "do you trust this third-party code", and these
/// came out of the very build that is asking. A newer bundled version replaces an older installed one — as does
/// a rebuild of the same version, which is what a developer produces all day and what the version number cannot
/// see. Either way the plugin keeps its enabled state and its settings, and its pin follows the new bytes.
///
/// It never overrides the operator: a plugin they disabled stays disabled and is left untouched on disk, and a
/// version they updated past ours is not rolled back.
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

        // Each immediate subfolder of the bundled root is one plugin (its dll, deps.json and plugin.json). A
        // bundled plugin ships, so it is installed even when not there yet (installNew), while the shared rule
        // still respects a disabled plugin and an operator's newer version.
        return await new PluginSourceInstaller(_registrations, _logger)
            .InstallFromSourceFoldersAsync(Directory.EnumerateDirectories(bundledRoot), pluginsRoot, installNew: true, cancellationToken)
            .ConfigureAwait(false);
    }
}
