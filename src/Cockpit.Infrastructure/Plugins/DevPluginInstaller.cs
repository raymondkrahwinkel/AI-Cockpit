using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Plugins;

namespace Cockpit.Infrastructure.Plugins;

// AC-1013: DEBUG-only dev convenience refreshing installed first-party plugins from freshly built output —
// only refreshes (never installs new, never touches disabled/newer, a build must never decide what a
// cockpit carries), finding `plugins-dev` by walking up; no-op off a dev checkout.
public sealed class DevPluginInstaller(ILogger? logger = null)
{
    private const string PluginsDevFolderName = "plugins-dev";
    private const int MaxParentWalk = 12;

    private readonly IPluginRegistrationStore _registrations = new PluginRegistrationStore();

    // The ids refreshed, for logging; empty when not on a dev checkout or nothing changed.
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

    // The `plugins-dev` folder for the running app's own checkout, or null off a dev checkout — the same
    // walk `InstallAsync` uses to decide there is nothing to sync. Exposed so a watcher (AC-185) can
    // find what to watch without duplicating that walk.
    public static string? FindPluginsDevRoot() =>
        _FindPluginsDev(new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));

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
            // AC-1013: a .Tests project's bin also carries the plugin's manifest (copied via project reference)
            // but a test assembly closure — xunit, a duplicate Abstractions breaking shared type identity —
            // so it's excluded; never a plugin source.
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
