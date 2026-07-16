using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// One editable row in the instances list of <see cref="YouTrackSettingsControl"/> (#48): a label, instance
/// base URL, permanent token and optional default project short-name, plus a remove button. Exposes
/// <see cref="ToInstance"/> so the settings control can collect every row's current values on save, and
/// <see cref="IsBlank"/> so an untouched freshly-added row (or one emptied back out) is dropped instead of
/// persisted as a junk entry.
/// </summary>
internal sealed class YouTrackInstanceRowControl : UserControl
{
    private readonly TextBox _label;
    private readonly TextBox _instanceUrl;
    private readonly TextBox _token;
    private readonly TextBox _defaultProjectTag;
    private readonly CheckBox _addMcp;

    public event Action? RemoveRequested;

    public YouTrackInstanceRowControl(YouTrackInstance instance)
    {
        _label = new TextBox { Text = instance.Label, PlaceholderText = "e.g. Team A" };
        _instanceUrl = new TextBox { Text = instance.InstanceUrl, PlaceholderText = "https://<instance>.youtrack.cloud/api" };
        _token = new TextBox { Text = instance.Token, PlaceholderText = "permanent token", PasswordChar = '•' };
        _defaultProjectTag = new TextBox { Text = instance.DefaultProjectTag, PlaceholderText = "default project short-name (optional)" };
        _addMcp = new CheckBox { Content = "Add this instance's MCP server to sessions", IsChecked = instance.AddMcpToSessions, FontSize = 11 };

        var remove = new Button { Content = "✕ Remove", FontSize = 11, Padding = new Thickness(8, 2) };
        remove.Click += (_, _) => RemoveRequested?.Invoke();

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(remove, Dock.Right);
        header.Children.Add(remove);
        header.Children.Add(new TextBlock { Text = "Label", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });

        Content = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    header,
                    _label,
                    _Label("Instance base URL"),
                    SettingsHelpRow.Build(_instanceUrl, "Your YouTrack REST API base, e.g. https://<instance>.youtrack.cloud/api (self-hosted: https://<host>/youtrack/api)."),
                    _Label("Permanent token"),
                    SettingsHelpRow.Build(_token, "Permanent token. In YouTrack: profile -> Account Security -> New token (scope: YouTrack)."),
                    _Label("Default project short-name (optional — preselected in the issues dialog)"),
                    SettingsHelpRow.Build(_defaultProjectTag, "Optional project short name (e.g. the prefix in issue IDs like PROJ-123), preselected in the issues dialog's project filter when this instance is picked. Leave empty to default to \"All\"."),
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

    private static TextBlock _Label(string text) => new() { Text = text, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
