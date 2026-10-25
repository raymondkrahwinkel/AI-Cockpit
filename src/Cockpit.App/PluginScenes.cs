using System.Reflection;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.App.Views;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Docking;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;
using Material.Icons;

namespace Cockpit.App;

// `--scene plugin:<name>` (AC-1711): a dev hook that renders a plugin's own view, which Cockpit.App cannot reference.
// It reads public static `Scenes` (name → Func<Control>) and `WorkspaceScenes` tables off the uiEntryType of each plugin
// built under plugins-dev. Not a plugin contract: a plugin without them offers nothing, and off a dev checkout neither.
internal static class PluginScenes
{
    public const string Prefix = "plugin:";

    public static Window Build(string scene, int width, int height)
    {
        var name = scene[Prefix.Length..];
        var (views, workspaces) = _Load();
        if (workspaces.TryGetValue(name, out var workspace))
        {
            return _MainWindowWith(workspace());
        }

        if (views.TryGetValue(name, out var create) == false)
        {
            var known = views.Keys.Concat(workspaces.Keys).Order(StringComparer.Ordinal).Select(key => Prefix + key).ToList();
            throw new ArgumentException(
                $"Unknown plugin scene \"{name}\". Known plugin scenes: {(known.Count == 0 ? "none (no plugin under plugins-dev is built with a Scenes table)" : string.Join(", ", known))}.",
                nameof(scene));
        }

        // A name ending in -floating renders in the host's own dock-panel window (AC-1710), titled by the view's
        // automation name, the way a CanFloat panel shows once undocked.
        var view = create();
        if (name.EndsWith("-floating", StringComparison.Ordinal))
        {
            var title = AutomationProperties.GetName(view) ?? name;
            var panel = new DockPanelRegistration(Prefix + name, title, MaterialIconKind.DockWindow, () => view);
            return new Docking.DockPanelWindow(panel, view, boundsStore: null) { Width = width, Height = height };
        }

        return new Window { Width = width, Height = height, Content = view };
    }

    // The real main window with the plugin's workspace active: its header buttons on the tab strip and its status
    // text in the status bar, as the operator sees them.
    private static Window _MainWindowWith((WorkspaceTypeRegistration Registration, string? StatusText) workspace)
    {
        var registry = new WorkspaceTypeRegistry(new ServiceCollection().BuildServiceProvider());
        registry.Register(workspace.Registration, new SceneStorage(), NullCockpitSessionObserver.Instance);
        var cockpit = new ViewModels.CockpitViewModel(workspaceTypeRegistry: registry);
        cockpit.Workspaces.OpenWorkspaceAsync(workspace.Registration.Id).GetAwaiter().GetResult();
        cockpit.SetPluginStatusText(workspace.Registration.Id.Split('.')[0], workspace.StatusText);
        return new MainWindow { DataContext = cockpit };
    }

    // Built output sits at plugins-dev/<plugin>/bin/<config>/<tfm>/, matching the running app's own config and tfm.
    private static (Dictionary<string, Func<Control>> Views, Dictionary<string, Func<(WorkspaceTypeRegistration, string?)>> Workspaces) _Load()
    {
        var views = new Dictionary<string, Func<Control>>(StringComparer.Ordinal);
        var workspaces = new Dictionary<string, Func<(WorkspaceTypeRegistration, string?)>>(StringComparer.Ordinal);
        var appDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (DevPluginInstaller.FindPluginsDevRoot() is not { } pluginsDev || appDir.Parent is not { } configuration)
        {
            return (views, workspaces);
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

            var type = Assembly.LoadFrom(Path.Combine(output, assembly.GetString() ?? string.Empty)).GetType(entryType.GetString() ?? string.Empty);
            _Merge(views, type?.GetProperty("Scenes", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IReadOnlyDictionary<string, Func<Control>>);
            _Merge(workspaces, type?.GetProperty("WorkspaceScenes", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IReadOnlyDictionary<string, Func<(WorkspaceTypeRegistration, string?)>>);
        }

        return (views, workspaces);
    }

    private static void _Merge<T>(Dictionary<string, T> into, IReadOnlyDictionary<string, T>? table)
    {
        foreach (var (key, value) in table ?? new Dictionary<string, T>())
        {
            into.TryAdd(key, value);
        }
    }

    private sealed class SceneStorage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _values = [];

        public T? Get<T>(string key) => _values.TryGetValue(key, out var value) && value is T typed ? typed : default;

        public void Set<T>(string key, T value) => _values[key] = value;

        public void Remove(string key) => _values.Remove(key);
    }
}
