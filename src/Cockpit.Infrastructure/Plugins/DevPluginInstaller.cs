using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Plugins;

namespace Cockpit.Infrastructure.Plugins;

/// <summary>
/// A developer-machine convenience (DEBUG only): refreshes already-installed first-party plugins from their
/// freshly built output, so a rebuild of a plugin lands in the running sandbox without a hand copy. It closes,
/// for the inner loop, the same "installed copy does not move with source" gap the bundled installer closes for
/// the plugins this build ships — but for the ones installed from the store, which a normal build never touches.
/// <para>
/// It only <em>refreshes</em>: a plugin that is not installed is not silently installed just because the repo
/// can build it (<c>installNew: false</c>), and a disabled or operator-newer plugin is left alone — the looseness
/// is the point, a build must never decide what a cockpit carries. It finds <c>plugins-dev</c> by walking up from
/// the running app and matching the app's own build config and target framework; off a dev checkout it finds
/// nothing and does nothing, which is exactly right in a release.
/// </para>
/// </summary>
public sealed class DevPluginInstaller(ILogger? logger = null)
{
    private const string PluginsDevFolderName = "plugins-dev";
    private const int MaxParentWalk = 12;

    private readonly IPluginRegistrationStore _registrations = new PluginRegistrationStore();

    /// <returns>The ids refreshed, for logging; empty when not on a dev checkout or nothing changed.</returns>
    public async Task<IReadOnlyList<string>> InstallAsync(string pluginsRoot, CancellationToken cancellationToken = default)
    {
        if (_ResolveSourceFolders() is not { Count: > 0 } sourceFolders)
        {
            return [];
        }

        return await new PluginSourceInstaller(_registrations, logger)
            .InstallFromSourceFoldersAsync(sourceFolders, pluginsRoot, installNew: false, cancellationToken)
            .ConfigureAwait(false);
    }

    // Each first-party plugin's built output lives at plugins-dev/<plugin>/bin/<config>/<tfm>/, next to its
    // plugin.json. Config and tfm are read from the running app's own base directory so the sync always matches
    // the build that is running, rather than a guessed "Debug/net10.0".
    private static IReadOnlyList<string> _ResolveSourceFolders()
    {
        var appDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var targetFramework = appDir.Name;
        var configuration = appDir.Parent?.Name;
        if (configuration is null)
        {
            return [];
        }

        var pluginsDev = _FindPluginsDev(appDir);
        if (pluginsDev is null)
        {
            return [];
        }

        var sources = new List<string>();
        foreach (var pluginDir in Directory.EnumerateDirectories(pluginsDev))
        {
            // A test project (Cockpit.Plugin.X.Tests) lives in plugins-dev too and, because it references the
            // plugin, copies the plugin's plugin.json into its own output. Its bin therefore carries a manifest
            // with the plugin's id but a whole test assembly closure — xunit, and a duplicate
            // Cockpit.Plugins.Abstractions that would break the shared type's identity. It is never a plugin source.
            if (Path.GetFileName(pluginDir).EndsWith(".Tests", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var outputDir = Path.Combine(pluginDir, "bin", configuration, targetFramework);
            if (File.Exists(Path.Combine(outputDir, "plugin.json")))
            {
                sources.Add(outputDir);
            }
        }

        return sources;
    }

    private static string? _FindPluginsDev(DirectoryInfo start)
    {
        var directory = start;
        for (var depth = 0; depth < MaxParentWalk && directory is not null; depth++)
        {
            var candidate = Path.Combine(directory.FullName, PluginsDevFolderName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
