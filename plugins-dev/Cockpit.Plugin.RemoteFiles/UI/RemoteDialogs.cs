using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Cockpit.Plugin.RemoteFiles.Contracts;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.RemoteFiles.UI;

// The trust decision sits between Test and the login, so it is a dialog that cannot be skipped; Cancel has the focus.
internal static class RemoteDialogs
{
    public static Control Trust(RemoteConnection connection, RemotePresented presented, Action trust)
    {
        var certificate = presented.Algorithm == "certificate";
        var where = $"{connection.Host}:{connection.Port}";
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(RemoteFilesSettingsControl.Hint(certificate
            ? $"{where} presents a certificate that this computer does not trust. Check the fingerprint with the server's administrator before you trust it."
            : $"{where} presents this host key. Compare it with the key on the server before you trust it, for example with ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub."));
        body.Children.Add(certificate
            ? _Facts(("Subject", presented.Subject ?? ""), ("Issuer", presented.Issuer ?? ""),
                ("Valid until", presented.NotAfter?.ToString("yyyy-MM-dd") ?? ""), ("SHA-256", presented.Fingerprint))
            : _Facts(("Algorithm", presented.Algorithm), ("Fingerprint", presented.Fingerprint)));
        body.Children.Add(_Note(MaterialIconKind.LockOutline,
            $"Nothing has been sent to {connection.Host} yet: no user name, password or key. Cockpit signs in only after you trust this {(certificate ? "certificate" : "key")}."));
        return _Dialog(body, ("Cancel", "Ghost", null), ("Trust and connect", "Accent", trust));
    }

    public static Control Changed(RemoteConnection connection, RemotePresented presented)
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(RemoteFilesSettingsControl.Refusal("Connection refused",
            $"{connection.Host}:{connection.Port} presented a different {(presented.Algorithm == "certificate" ? "certificate" : "host key")} than the one you trusted. That happens when the server was reinstalled, or when something sits between you and the server. Cockpit closed the connection before signing in and sent nothing."));
        body.Children.Add(_Facts(("You trusted", $"{connection.Pin?.Fingerprint}\n{connection.Pin?.Algorithm} · trusted on {connection.Pin?.Trusted.LocalDateTime:yyyy-MM-dd}"),
            ("Presented now", $"{presented.Fingerprint}\n{presented.Algorithm}")));
        body.Children.Add(RemoteFilesSettingsControl.Hint("If you expect this change, confirm the new key with whoever runs the server. Then forget the old key in this connection's settings and test the connection again."));
        return _Dialog(body, ("Close", "Accent", null));
    }

    public static Control Forget(RemoteConnection connection, Action forget)
    {
        var body = RemoteFilesSettingsControl.Hint(
            $"Cockpit stops trusting {connection.Pin?.Fingerprint} for {connection.Name}. The next Test connection shows the key the server presents, and you decide again. Nothing connects until then.");
        return _Dialog(body, ("Cancel", "Ghost", null), ("Forget key", "Accent", forget));
    }

    private static Control _Facts(params (string Label, string Value)[] facts)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, RowSpacing = 6 };
        foreach (var (label, value) in facts)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var name = RemoteFilesSettingsControl.Hint(label);
            var text = RemoteFilesSettingsControl.Mono(value);
            Grid.SetRow(name, grid.RowDefinitions.Count - 1);
            Grid.SetRow(text, grid.RowDefinitions.Count - 1);
            Grid.SetColumn(text, 1);
            grid.Children.Add(name);
            grid.Children.Add(text);
        }
        var box = new Border { Padding = new Thickness(12, 10), Child = grid };
        box.Bind(Border.BackgroundProperty, box.GetResourceObservable("CockpitInsetBgBrush"));
        box.Bind(Border.CornerRadiusProperty, box.GetResourceObservable("CockpitControlRadius"));
        return box;
    }

    private static Control _Note(MaterialIconKind kind, string text)
    {
        var icon = new MaterialIcon { Kind = kind, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Top };
        icon.Bind(MaterialIcon.ForegroundProperty, icon.GetResourceObservable("CockpitStatusDoneBrush"));
        var hint = RemoteFilesSettingsControl.Hint(text);
        hint.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(icon, Dock.Left);
        return new DockPanel { Children = { icon, hint } };
    }

    private static Control _Dialog(Control body, params (string Label, string Style, Action? Then)[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        Button? first = null;
        var layout = new DockPanel { Margin = new Thickness(16) };
        foreach (var (label, style, then) in buttons)
        {
            var button = new Button { Content = label, Classes = { style } };
            button.Click += (_, _) =>
            {
                (TopLevel.GetTopLevel(layout) as Window)?.Close();
                then?.Invoke();
            };
            first ??= button;
            row.Children.Add(button);
        }
        DockPanel.SetDock(row, Dock.Bottom);
        row.Margin = new Thickness(0, 12, 0, 0);
        layout.Children.Add(row);
        layout.Children.Add(new ScrollViewer { Content = body });
        layout.AttachedToVisualTree += (_, _) => first?.Focus();
        return layout;
    }
}
