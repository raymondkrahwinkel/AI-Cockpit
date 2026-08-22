using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.YouTrack;

// One editable row in the instances list of `YouTrackSettingsControl` (#48): a label, instance
// base URL, permanent token and optional default project short-name, plus a remove button. Exposes
// `ToInstance` so the settings control can collect every row's current values on save, and
// `IsBlank` so an untouched freshly-added row (or one emptied back out) is dropped instead of
// persisted as a junk entry.
internal sealed class YouTrackInstanceRowControl : UserControl
{
    private readonly TextBox _label;
    private readonly TextBox _instanceUrl;
    private readonly TextBox _token;
    private readonly TextBox _defaultProjectTag;
    private readonly CheckBox _addMcp;

    public event Action? RemoveRequested;

    public YouTrackInstanceRowControl(ICockpitHost host, YouTrackInstance instance)
    {
        _label = new TextBox { Text = instance.Label, PlaceholderText = "e.g. Team A" };
        _instanceUrl = new TextBox { Text = instance.InstanceUrl, PlaceholderText = "https://<instance>.youtrack.cloud/api" };
        _token = new TextBox { Text = instance.Token, PlaceholderText = "permanent token", PasswordChar = '•' };
        _defaultProjectTag = new TextBox { Text = instance.DefaultProjectTag, PlaceholderText = "default project short-name (optional)" };
        _addMcp = new CheckBox { Content = "Add this instance's MCP server to sessions", IsChecked = instance.AddMcpToSessions, FontSize = 11 };

        var remove = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    new MaterialIcon { Kind = MaterialIconKind.Close, Width = 12, Height = 12 },
                    new TextBlock { Text = "Remove", VerticalAlignment = VerticalAlignment.Center },
                },
            },
            FontSize = 11,
            Padding = new Thickness(8, 2),
        };
        remove.Click += (_, _) => RemoveRequested?.Invoke();

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(remove, Dock.Right);
        header.Children.Add(remove);
        header.Children.Add(new TextBlock { Text = "Label", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });

        Content = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            CornerRadius = _Radius("CockpitControlRadius", 9),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    header,
                    _label,
                    _LabelRow("Instance base URL", host.CreateHelpHint("setup", "base-url")),
                    _instanceUrl,
                    _LabelRow("Permanent token", host.CreateHelpHint("setup", "permanent-token")),
                    _token,
                    _LabelRow("Default project short-name (optional — preselected in the issues dialog)", host.CreateHelpHint("setup", "project-short-name")),
                    _defaultProjectTag,
                    _addMcp,
                    new TextBlock
                    {
                        Text = "When on, this instance's YouTrack tools are offered to every session (and can be unticked per session when you start one). Managed here — it does not appear in the MCP servers dialog.",
                        FontSize = 11,
                        Opacity = 0.7,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        };
    }

    public bool IsBlank =>
        string.IsNullOrWhiteSpace(_label.Text)
        && string.IsNullOrWhiteSpace(_instanceUrl.Text)
        && string.IsNullOrWhiteSpace(_token.Text)
        && string.IsNullOrWhiteSpace(_defaultProjectTag.Text);

    public YouTrackInstance ToInstance() => new(
        string.IsNullOrWhiteSpace(_label.Text) ? "Untitled" : _label.Text.Trim(),
        string.IsNullOrWhiteSpace(_instanceUrl.Text) ? string.Empty : _instanceUrl.Text.Trim().TrimEnd('/'),
        _token.Text?.Trim() ?? string.Empty,
        _defaultProjectTag.Text?.Trim() ?? string.Empty,
        _addMcp.IsChecked ?? true);

    // AC-1033: a label with the SDK-drawn "?" beside it instead of the old `SettingsHelpRow` tooltip, pointing at
    // the section of this plugin's own setup page that explains the field below.
    private static StackPanel _LabelRow(string text, Control help) => new()
    {
        Orientation = Orientation.Horizontal,
        Margin = new Thickness(0, 4, 0, 0),
        Children = { new TextBlock { Text = text, FontSize = 11 }, help },
    };

    // The host's geometry token, so a plugin's box rounds like the app's other boxes.
    private static CornerRadius _Radius(string key, double fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is CornerRadius radius
            ? radius
            : new CornerRadius(fallback);

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
