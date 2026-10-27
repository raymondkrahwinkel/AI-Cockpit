using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugin.Browser.UI.Runtime;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Browser.UI;

// The plugin settings: which engine this plugin version brings, and whether it has aged past the point where updating
// is advised (AC-1727, information only), and whether agents get the Cockpit browser (AC-1712, saved on OK).
internal sealed class BrowserEngineInfoControl : UserControl, IPluginSettingsView
{
    // The backend half reads the same key (BrowserSettings); the UI half never references that assembly.
    private const string AgentsUseCockpitBrowserKey = "agentsUseCockpitBrowser";

    private readonly IPluginStorage _storage;
    private readonly CheckBox _agentsUseCockpitBrowser;
    private readonly BrowserTabSession _tabSession;
    private readonly ComboBox _startupMode;
    private readonly TextBox _startPage;

    public BrowserEngineInfoControl(CefRuntimeCatalog catalog, IPluginStorage storage, BrowserTabSession tabSession)
    {
        _storage = storage;
        _tabSession = tabSession;
        _agentsUseCockpitBrowser = new CheckBox
        {
            Content = "Agents use the Cockpit browser",
            IsChecked = storage.Get<bool?>(AgentsUseCockpitBrowserKey) ?? true,
        };
        ToolTip.SetTip(_agentsUseCockpitBrowser, "A session with Playwright MCP gets its own headless Chromium with an empty profile. Off: Playwright starts its own browser, as before.");
        _startupMode = new ComboBox
        {
            ItemsSource = new[] { "Restore previous tabs", "Empty new tab", "Start page (URL)" },
            SelectedIndex = (int)tabSession.StartupMode,
        };
        _startPage = new TextBox { Text = tabSession.StartPage, PlaceholderText = "https://example.com" };
        _startupMode.SelectionChanged += (_, _) => _startPage.IsVisible = _startupMode.SelectedIndex == (int)BrowserStartupMode.StartPage;
        _startPage.IsVisible = _startupMode.SelectedIndex == (int)BrowserStartupMode.StartPage;

        var panel = new StackPanel
        {
            Margin = new Thickness(4),
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Browser engine", FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = $"CEF {catalog.CefVersion} · Chromium {catalog.ChromiumVersion}" },
                new TextBlock { Text = $"Built {catalog.BuildDate:yyyy-MM-dd}" },
            },
        };

        if (catalog.IsOutdated(DateOnly.FromDateTime(DateTime.Today)))
        {
            panel.Children.Add(new TextBlock { Text = "Engine outdated · update the plugin", Foreground = Brushes.OrangeRed });
        }

        panel.Children.Add(new TextBlock { Text = "Agents", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(_agentsUseCockpitBrowser);
        panel.Children.Add(new TextBlock { Text = "On startup", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(_startupMode);
        panel.Children.Add(_startPage);
        Content = panel;
    }

    public bool TryStage(out Action? commit, out string? error)
    {
        var useCockpitBrowser = _agentsUseCockpitBrowser.IsChecked ?? true;
        var startupMode = Enum.IsDefined(typeof(BrowserStartupMode), _startupMode.SelectedIndex)
            ? (BrowserStartupMode)_startupMode.SelectedIndex
            : BrowserStartupMode.RestorePreviousTabs;
        var startPage = string.IsNullOrWhiteSpace(_startPage.Text) ? BrowserModel.StartAddress : _startPage.Text;
        commit = () =>
        {
            _storage.Set(AgentsUseCockpitBrowserKey, useCockpitBrowser);
            _tabSession.Configure(startupMode, startPage);
        };
        error = null;
        return true;
    }
}
