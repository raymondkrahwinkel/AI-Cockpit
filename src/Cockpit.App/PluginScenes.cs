using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Cockpit.Infrastructure.Plugins;

namespace Cockpit.App;

// `--scene plugin:<name>` (AC-1711): a dev hook that renders a plugin's own view, which Cockpit.App cannot reference.
// It reads a public static `Scenes` table (name → Func<Control>) off the uiEntryType of each plugin built under
// plugins-dev. Not a plugin contract: a plugin without the table offers nothing, and off a dev checkout there is none.
internal static class PluginScenes
{
    public const string Prefix = "plugin:";

    public static Window Build(string scene, int width, int height)
    {
        var name = scene[Prefix.Length..];
        var all = _Load();
        if (all.TryGetValue(name, out var create) == false)
        {
            throw new ArgumentException(
                $"Unknown plugin scene \"{name}\". Known plugin scenes: {(all.Count == 0 ? "none (no plugin under plugins-dev is built with a Scenes table)" : string.Join(", ", all.Keys.Order(StringComparer.Ordinal).Select(key => Prefix + key)))}.",
                nameof(scene));
        }

        return new Window { Width = width, Height = height, Content = create() };
    }

    // Built output sits at plugins-dev/<plugin>/bin/<config>/<tfm>/, matching the running app's own config and tfm.
    private static Dictionary<string, Func<Control>> _Load()
    {
        var scenes = new Dictionary<string, Func<Control>>(StringComparer.Ordinal);
        var appDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (DevPluginInstaller.FindPluginsDevRoot() is not { } pluginsDev || appDir.Parent is not { } configuration)
        {
            return scenes;
        }

        foreach (var pluginDir in Directory.EnumerateDirectories(pluginsDev))
        {
            // A .Tests project's bin carries a copy of the plugin's manifest and assemblies, never a plugin of its own.
            var output = Path.Combine(pluginDir, "bin", configuration.Name, appDir.Name);
            var manifest = Path.Combine(output, "plugin.json");
            if (pluginDir.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) || File.Exists(manifest) == false)
            {
                continue;
            }

            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            if (json.RootElement.TryGetProperty("uiAssembly", out var assembly) == false
                || json.RootElement.TryGetProperty("uiEntryType", out var entryType) == false)
            {
                continue;
            }

            var table = Assembly.LoadFrom(Path.Combine(output, assembly.GetString() ?? string.Empty))
                .GetType(entryType.GetString() ?? string.Empty)
                ?.GetProperty("Scenes", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as IReadOnlyDictionary<string, Func<Control>>;
            foreach (var (key, value) in table ?? new Dictionary<string, Func<Control>>())
            {
                scenes.TryAdd(key, value);
            }
        }

        return scenes;
    }
}
