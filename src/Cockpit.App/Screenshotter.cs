using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Plugins;

namespace Cockpit.App;

/// <summary>
/// Headless startup mode that renders a window off-screen via the Avalonia Skia headless platform and
/// writes a single frame to disk as PNG. Lets an external caller verify the UI layout without a display
/// attached (Iron Law #9: automated visual verification). <paramref name="scene"/> picks which window:
/// the main cockpit by default, or a dialog whose layout would otherwise be unverifiable.
/// </summary>
internal static class Screenshotter
{
    private const int DefaultWindowWidth = 1100;
    private const int DefaultWindowHeight = 760;

    public static void Run(string outputPngPath, int width = DefaultWindowWidth, int height = DefaultWindowHeight, string? scene = null)
    {
        BuildHeadlessAvaloniaApp().SetupWithoutStarting();

        Window window = scene switch
        {
            "about" => new AboutDialog { DataContext = ViewModels.AboutInfo.FromAssembly(typeof(Screenshotter).Assembly) },
            "single-instance" => new SingleInstanceNoticeDialog(),
            "options" => new OptionsDialog { DataContext = new ViewModels.CockpitViewModel() },
            "shortcuts" => _OptionsOnTab("Shortcuts"),
            "debug" => _OptionsOnTab("Debug"),
            "profiles" => new ManageProfilesDialog { DataContext = new ViewModels.ManageProfilesDialogViewModel(), Height = 900 },
            "new-session" => new NewSessionDialog { DataContext = new ViewModels.NewSessionDialogViewModel() },
            "plugin-store" => _PluginStore(),
            "manage-stores" => _ManageStores(),
            "tasks" => new DelegatedTasksDialog { DataContext = new ViewModels.DelegatedTasksViewModel() },
            "set-status" => new SetStatusDialog { DataContext = new ViewModels.SetStatusDialogViewModel("AC-32 — manual status") },
            "session" => new MainWindow { DataContext = new ViewModels.CockpitViewModel { GlobalSingleSessionLayout = true } },
            "tty" => new Window { Width = width, Height = height, Content = new Views.TtyView { DataContext = new ViewModels.TtyViewModel() } },
            // A plain terminal pane (#AC-25/#AC-29): its own scene so the shared header's terminal treatment
            // (kind chip "TTY", no plugin host, no usage pill, shell name only in the cwd tooltip) is verifiable
            // headless — the SDK-only 'session' scene is exactly what let the earlier TTY-header miss slip through.
            "terminal" => new Window { Width = width, Height = height, Content = new Views.TtyView { DataContext = ViewModels.TtyViewModel.DesignTerminal() } },
            "plugin-update-badge" => _PluginUpdateBadge(),
            "toolbar-actions" => _ToolbarActions(),
            _ => new MainWindow { DataContext = new ViewModels.CockpitViewModel() },
        };

        // A SizeToContent dialog measures itself; only the main window takes the requested size.
        if (window is MainWindow)
        {
            window.Width = width;
            window.Height = height;
        }

        window.Show();

        var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("Headless renderer produced no frame to capture.");

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPngPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        frame.Save(outputPngPath);
        window.Close();
    }

    // Renders the Options dialog with one of its tabs selected, so a tab other than the first one can be
    // verified without a display.
    private static OptionsDialog _OptionsOnTab(string header)
    {
        var dialog = new OptionsDialog { DataContext = new ViewModels.CockpitViewModel() };
        var tabs = dialog.FindControl<TabControl>("Tabs")
            ?? throw new InvalidOperationException("The Options dialog has no 'Tabs' TabControl to select on.");

        tabs.SelectedItem = tabs.Items
            .OfType<TabItem>()
            .FirstOrDefault(tab => string.Equals(tab.Header as string, header, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"The Options dialog has no '{header}' tab.");

        return dialog;
    }

    // Renders the sessions workspace with a couple of plugin toolbar actions seeded (AC-91) so the quick-action
    // buttons next to the workspace gear are verifiable headless.
    private static MainWindow _ToolbarActions()
    {
        var cockpit = new ViewModels.CockpitViewModel { GlobalSingleSessionLayout = true };
        cockpit.PluginToolbarActions.Add(new Plugins.PluginToolbarAction(
            "docker", new Cockpit.Plugins.Abstractions.ToolbarAction("Docker settings", Material.Icons.MaterialIconKind.Docker, () => Task.CompletedTask)));
        cockpit.PluginToolbarActions.Add(new Plugins.PluginToolbarAction(
            "kubernetes", new Cockpit.Plugins.Abstractions.ToolbarAction("Kubernetes settings", Material.Icons.MaterialIconKind.Kubernetes, () => Task.CompletedTask)));

        return new MainWindow { DataContext = cockpit };
    }

    // Renders the full window with a plugin-update count seeded (AC-76) so the sidebar "Plugin store" button's
    // coral update badge is verifiable headless.
    private static MainWindow _PluginUpdateBadge()
    {
        var cockpit = new ViewModels.CockpitViewModel { GlobalSingleSessionLayout = true };
        cockpit.Plugins.SetUpdateBadgeCount(3);

        return new MainWindow { DataContext = cockpit };
    }

    // Renders the plugin store (#62) with a sample catalogue seeded straight into the manager's collections
    // (no network browse — the dialog only loads on the real app's open), so its layout — the
    // categories | plugins | details columns, the Installed/Updates group pinned to the sidebar foot, the
    // list rows and their install-state — can be verified headless.
    private static PluginStoreDialog _PluginStore()
    {
        var manager = new PluginManagerViewModel();
        manager.Stores.Add(PluginStoreConfig.Remote("https://store.aicockpit.dev/index.json"));
        foreach (var row in _SampleStorePlugins())
        {
            manager.AvailablePlugins.Add(row);
        }

        var viewModel = new PluginStoreDialogViewModel(manager)
        {
            SelectedPlugin = manager.AvailablePlugins.FirstOrDefault(),
        };

        return new PluginStoreDialog { DataContext = viewModel };
    }

    // Renders the Manage-stores dialog (#62, AC-7) with a few sample stores seeded straight into the manager's
    // StoreInfos — a private remote one (a token, so the lock badge shows) with a logo, a public remote falling
    // back to a URL-derived name and default glyph, and a local-folder one (the folder badge) — so its layout and
    // the icon/name/count/badge rows can be verified headless.
    private static ManageStoresDialog _ManageStores()
    {
        var manager = new PluginManagerViewModel();
        // A real logo image (the app icon stands in for a store's own), so the screenshot shows the fetched-image
        // path rather than only the emoji fallback.
        manager.StoreInfos.Add(new PluginStoreInfo(PluginStoreConfig.Remote("https://github.com/aicockpit/plugins", "sample-token"))
        {
            Name = "AI-Cockpit Plugins", PluginCount = 13, IsReachable = true, IsBrowsed = true,
            Logo = _LoadAssetBitmap("avares://Cockpit.App/Assets/AppIcon.png"),
        });
        manager.StoreInfos.Add(new PluginStoreInfo(PluginStoreConfig.Remote("https://raw.githubusercontent.com/raymond/cockpit-extras/main/index.json"))
        {
            PluginCount = 4, IsReachable = true, IsBrowsed = true,
        });
        manager.StoreInfos.Add(new PluginStoreInfo(PluginStoreConfig.Local("/home/you/my-plugins"))
        {
            PluginCount = 2, IsReachable = true, IsBrowsed = true,
        });

        return new ManageStoresDialog { DataContext = manager };
    }

    private static Bitmap? _LoadAssetBitmap(string uri)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(uri));
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<StorePluginRowViewModel> _SampleStorePlugins()
    {
        static StorePluginRowViewModel Row(
            string id, string name, string description, string category, string version, string icon,
            bool featured, bool installed, bool hasSettings = false, bool homepage = false, bool repository = false)
        {
            var versions = new[] { new PluginStoreVersion(version, $"plugins/{id}.zip", null, null, null, null) };
            var entry = new PluginStoreEntry(
                id, name, description, "Cockpit", version, versions, category, icon,
                homepage ? $"https://aicockpit.dev/{id}" : null,
                repository ? $"https://github.com/aicockpit/{id}" : null,
                featured, "2026-07-10");
            // installedVersion == latest ⇒ shown as installed and up to date (a green "Installed" pill),
            // null ⇒ available (the accent "Install" call-to-action).
            return new StorePluginRowViewModel(entry, PluginStoreConfig.Remote("https://store.aicockpit.dev/index.json"),
                installed ? version : null, isEnabled: installed, hasSettings: hasSettings);
        }

        return
        [
            Row("github-issues", "GitHub Issues", "Browse open GitHub issues across your repos (via the gh CLI) or one repo in a dedicated panel.", "Issue trackers", "1.8.0", "🐙", featured: true, installed: false, homepage: true, repository: true),
            Row("workflows", "Workflows", "A visual editor for cockpit workflows, and an engine that runs them: drop steps onto a canvas and wire them up.", "Automation", "0.22.0", "🔀", featured: true, installed: true, homepage: true, repository: true),
            Row("claude-bundled", "Claude (bundled)", "Claude as a provider plugin (Fase 4). Runs the real interactive Claude TUI in a session panel.", "AI providers", "0.3.1", "🌸", featured: false, installed: true, homepage: true),
            Row("clock", "Clock", "The time and date, for a Dashboard workspace. Ships with the cockpit, so it is always there.", "Widgets", "1.0.0", "🕐", featured: false, installed: true),
            Row("system-monitor", "System Monitor", "CPU, memory and disk usage for a Dashboard workspace. You pick which stats show.", "Widgets", "1.0.0", "🖥", featured: false, installed: true),
            Row("git-status", "Git status", "A git indicator in every session — a coloured dot and the branch, so you always know the repo state.", "Productivity", "1.4.0", "🌱", featured: false, installed: true, hasSettings: true, repository: true),
            Row("transcript-search", "Claude Transcript Search", "Search everything you and the agent ever wrote in a Claude CLI session.", "Productivity", "1.2.0", "🔍", featured: false, installed: true, repository: true),
            Row("codex-provider", "CLI Agent Provider (Codex)", "Adds Codex CLI as a selectable session provider, driven as a subprocess per session.", "AI providers", "0.2.0", "🧩", featured: false, installed: false, homepage: true, repository: true),
            Row("gemini-openai", "Gemini / OpenAI Provider", "Adds Gemini and OpenAI models as selectable session providers, keyed per profile.", "AI providers", "0.4.0", "✨", featured: false, installed: true, hasSettings: true, homepage: true, repository: true),
        ];
    }

    private static AppBuilder BuildHeadlessAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseSkia()
            .With(Program.CockpitFontOptions())
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
            });
}
