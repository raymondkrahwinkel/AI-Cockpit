using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Cockpit.Plugin.RemoteFiles.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.RemoteFiles.UI;

// The backend tests and stores where the plugin runs, so one view serves the desktop and a server's admin connection.
internal sealed class RemoteFilesSettingsControl : UserControl, IAsyncPluginSettingsView
{
    private static readonly (string Kind, string Label)[] Kinds =
        [(RemoteFilesChannel.WebDav, "WebDAV"), (RemoteFilesChannel.Sftp, "SFTP"), (RemoteFilesChannel.Ftps, "FTPS"), (RemoteFilesChannel.Ftp, "FTP — unencrypted")];

    private readonly IPluginUiChannel _channel;
    private readonly Func<string, Func<Control>, Task> _showDialog;
    private readonly List<RemoteConnection> _connections = [];
    private readonly Dictionary<string, RemoteSecretsSet> _set = [];
    private readonly Dictionary<string, RemoteSecrets> _typed = [];
    private readonly Dictionary<string, string> _results = [];
    private readonly HashSet<string> _staged = [];
    private readonly StackPanel _rows = new() { Spacing = 6 };
    private readonly Border _banner;
    private readonly ContentControl _refusal = new();
    private string? _open;
    private bool _remote;
    private Button? _test;
    private TextBlock? _result;

    public RemoteFilesSettingsControl(IPluginUiChannel channel, Func<string, Func<Control>, Task> showDialog)
    {
        _channel = channel;
        _showDialog = showDialog;
        var add = new Button { Content = "+ Add connection", Margin = new Thickness(0, 4, 0, 0) };
        add.Click += (_, _) => _Add();
        _banner = _Banner();
        Content = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                _banner,
                new TextBlock { Text = "CONNECTIONS", Classes = { "optionsSectionHeader" } },
                Hint("Each connection appears in the Files browser. Passwords and keys are stored as secrets; this page only shows whether one is set."),
                _rows,
                _refusal,
                add,
            },
        };
        Ready = LoadAsync();
    }

    internal Task Ready { get; }

    public bool TryStage(out Func<Task>? commit, out string? error)
    {
        var request = new RemoteSaveRequest(_connections.ToArray(), new Dictionary<string, RemoteSecrets>(_typed));
        commit = async () =>
        {
            var answer = await _InvokeAsync<RemoteConnectionsAnswer>(RemoteFilesChannel.Save, request).ConfigureAwait(true);
            if (answer?.Error is { } refused)
            {
                throw new InvalidOperationException(refused);
            }
            foreach (var (id, set) in answer?.Secrets ?? new Dictionary<string, RemoteSecretsSet>())
            {
                _set[id] = set;
            }
            _typed.Clear();
            _staged.Clear();
            _Render();
        };
        error = null;
        return true;
    }

    private async Task LoadAsync()
    {
        try
        {
            var answer = await _InvokeAsync<RemoteConnectionsAnswer>(RemoteFilesChannel.Connections, new { }).ConfigureAwait(true);
            _connections.AddRange(answer?.Connections ?? []);
            foreach (var (id, set) in answer?.Secrets ?? new Dictionary<string, RemoteSecretsSet>())
            {
                _set[id] = set;
            }
            _remote = answer?.Machine is { } machine && machine != Environment.MachineName;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _refusal.Content = Refusal("Connections not loaded", "The plugin could not be asked for its connections. Close this page and try again.");
        }
        _banner.IsVisible = _remote;
        _Render();
    }

    internal void Expand(string? id)
    {
        _open = id;
        _Render();
    }

    private void _Add()
    {
        var connection = new RemoteConnection(Guid.NewGuid().ToString("n"), "", RemoteFilesChannel.Sftp, "", 22, "");
        _connections.Add(connection);
        _open = connection.Id;
        _Render();
    }

    private void _Render()
    {
        _rows.Children.Clear();
        if (_connections.Count == 0)
        {
            _rows.Children.Add(Hint("No connections yet. The Files browser shows nothing until you add one."));
        }
        foreach (var connection in _connections.ToList())
        {
            _rows.Children.Add(_Row(connection));
        }
    }

    private void _Update(string id, Func<RemoteConnection, RemoteConnection> change, bool render = false)
    {
        var index = _connections.FindIndex(connection => connection.Id == id);
        if (index >= 0)
        {
            _connections[index] = change(_connections[index]);
        }
        if (render)
        {
            _Render();
        }
    }

    private RemoteConnection _Get(string id) => _connections.First(connection => connection.Id == id);

    private Border _Row(RemoteConnection connection)
    {
        var open = _open == connection.Id;
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(_Icon(open ? MaterialIconKind.ChevronDown : MaterialIconKind.ChevronRight));
        title.Children.Add(new TextBlock { Text = connection.Name is { Length: > 0 } name ? name : "New connection", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(Mono(connection.Kind == RemoteFilesChannel.WebDav ? connection.DavUrl : $"{connection.Kind}://{(connection.User is { Length: > 0 } user ? user : "user")}@{(connection.Host is { Length: > 0 } host ? host : "host")}:{connection.Port}"));
        title.Children.Add(_Chip(connection));
        if (connection.ReadOnly)
        {
            title.Children.Add(_Pill("Read only"));
        }
        var header = new Button { Classes = { "Subtle" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Content = title };
        AutomationProperties.SetName(header, $"{(open ? "Collapse" : "Expand")} {connection.Name}");
        header.Click += (_, _) =>
        {
            _open = open ? null : connection.Id;
            _Render();
        };
        var body = new StackPanel { Spacing = 8, Children = { header } };
        if (open)
        {
            body.Children.Add(_Editor(connection));
        }
        var row = new Border { Padding = new Thickness(8, 6), BorderThickness = new Thickness(1), Child = body };
        row.Bind(Border.BackgroundProperty, row.GetResourceObservable("CockpitSecondaryBgBrush"));
        row.Bind(Border.BorderBrushProperty, row.GetResourceObservable("CockpitHairlineBrush"));
        row.Bind(Border.CornerRadiusProperty, row.GetResourceObservable("CockpitControlRadius"));
        return row;
    }

    private Control _Chip(RemoteConnection connection) => connection switch
    {
        { Kind: RemoteFilesChannel.Ftp } => _Pill("Unencrypted", "CockpitStatusErrorBrush"),
        { Pin: not null } => _Pill(_staged.Contains(connection.Id) ? "Will trust on Apply" : connection.Kind == RemoteFilesChannel.Sftp ? "Host key trusted" : "Certificate trusted", "CockpitStatusDoneBrush"),
        { Kind: RemoteFilesChannel.WebDav } when connection.DavUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) => _Pill("Unencrypted", "CockpitStatusErrorBrush"),
        { Kind: RemoteFilesChannel.Ftps or RemoteFilesChannel.WebDav } => _Pill("System certificate check"),
        _ => _Pill("Not verified", "CockpitStatusWaitingBrush"),
    };

    private Control _Editor(RemoteConnection connection)
    {
        var id = connection.Id;
        var sftp = connection.Kind == RemoteFilesChannel.Sftp;
        var dav = connection.Kind == RemoteFilesChannel.WebDav;
        var editor = new StackPanel { Spacing = 8, Margin = new Thickness(8, 0, 8, 8) };

        var name = new TextBox { Text = connection.Name, PlaceholderText = "Name in the Files browser" };
        name.TextChanged += (_, _) => _Update(id, c => c with { Name = name.Text?.Trim() ?? "" });
        var type = new ComboBox { ItemsSource = Kinds.Select(kind => kind.Label).ToArray(), SelectedIndex = Array.FindIndex(Kinds, kind => kind.Kind == connection.Kind), MinWidth = 180 };
        type.SelectionChanged += (_, _) => _Update(id, c => c with
        {
            Kind = Kinds[type.SelectedIndex].Kind,
            Port = c.Port == RemoteFilesChannel.DefaultPort(c.Kind) ? RemoteFilesChannel.DefaultPort(Kinds[type.SelectedIndex].Kind) : c.Port,
            Pin = null,
            UseKey = c.UseKey && Kinds[type.SelectedIndex].Kind == RemoteFilesChannel.Sftp,
        }, render: true);
        editor.Children.Add(_Fields(("Name", name), ("Type", type)));
        if (connection.Kind == RemoteFilesChannel.Ftp)
        {
            editor.Children.Add(Refusal("Unencrypted connection",
                "FTP sends the password and every file in plain text. Anyone on the network path can read or change them. Use SFTP or FTPS if the server supports it."));
        }

        var host = new TextBox { Text = connection.Host, PlaceholderText = "files.example.com" };
        host.TextChanged += (_, _) => _Update(id, c => c with { Host = host.Text?.Trim() ?? "" });
        var port = new NumericUpDown { Value = connection.Port, Minimum = 1, Maximum = 65535, FormatString = "0", Width = 150 };
        port.ValueChanged += (_, _) => _Update(id, c => c with { Port = (int)(port.Value ?? c.Port) });
        var user = new TextBox { Text = connection.User };
        user.TextChanged += (_, _) => _Update(id, c => c with { User = user.Text?.Trim() ?? "" });
        if (dav)
        {
            var url = new TextBox { Text = connection.DavUrl, PlaceholderText = "https://files.example.com/remote.php/dav/files/user/" };
            url.TextChanged += (_, _) => _Update(id, c => c with { DavUrl = url.Text?.Trim() ?? "" });
            url.LostFocus += (_, _) => _Render();
            editor.Children.Add(_Fields(("WebDAV folder URL", url)));
            var bearer = new ToggleSwitch { IsChecked = connection.UseBearer, OnContent = "Bearer token", OffContent = "Basic authentication" };
            bearer.IsCheckedChanged += (_, _) => _Update(id, c => c with { UseBearer = bearer.IsChecked == true }, render: true);
            editor.Children.Add(_Fields(("Authentication", bearer)));
            if (connection.DavUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                editor.Children.Add(Refusal("Unencrypted connection", "HTTP sends the password or token and every file in plain text. Anyone on the network path can read or change them. Use HTTPS if the server supports it."));
            }
        }
        else
        {
            editor.Children.Add(_Fields(("Host", host), ("Port", port)));
        }
        editor.Children.Add(_Fields(("User name", user)));

        var set = _set.GetValueOrDefault(id) ?? new RemoteSecretsSet(false, false, false);
        if (sftp)
        {
            var password = new RadioButton { Content = "Password", GroupName = "auth-" + id, IsChecked = !connection.UseKey };
            var key = new RadioButton { Content = "Private key", GroupName = "auth-" + id, IsChecked = connection.UseKey };
            key.IsCheckedChanged += (_, _) => _Update(id, c => c with { UseKey = key.IsChecked == true }, render: true);
            editor.Children.Add(_Fields(("Sign in with", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { password, key } })));
        }
        if (sftp && connection.UseKey)
        {
            var load = new Button { Content = "Load from file…", Classes = { "Compact" } };
            load.Click += async (_, _) => await _LoadKeyAsync(id).ConfigureAwait(true);
            var typedKey = _typed.GetValueOrDefault(id)?.Key is { Length: > 0 };
            editor.Children.Add(_Fields(("Private key", new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { load, typedKey ? _Pill("Loaded, saved with Apply", "CockpitStatusDoneBrush") : _SetPill(set.Key) },
            })));
            editor.Children.Add(Hint($"Cockpit stores the key itself, not the path to the file{(_remote ? ". The server cannot see your desktop's files" : "")}."));
            editor.Children.Add(_Secret(id, "Passphrase", set.Passphrase, "Leave blank if the key has none", (s, v) => s with { Passphrase = v }));
        }
        else
        {
            editor.Children.Add(_Secret(id, dav && connection.UseBearer ? "Bearer token" : "Password", set.Password, "Password or token", (s, v) => s with { Password = v }));
        }

        var readOnly = new ToggleSwitch { IsChecked = connection.ReadOnly, OnContent = "Read only", OffContent = "Read and write" };
        readOnly.IsCheckedChanged += (_, _) => _Update(id, c => c with { ReadOnly = readOnly.IsChecked == true });
        editor.Children.Add(_Fields(("Access", readOnly)));
        editor.Children.Add(Hint("Read only: the Files browser can open and download, but not save, upload or create folders. Delete and rename are not part of this version."));

        editor.Children.Add(new TextBlock { Text = sftp ? "HOST KEY" : "CERTIFICATE", Classes = { "optionsSectionHeader" } });
        editor.Children.Add(_TrustBlock(connection));

        var test = new Button { Content = "Test connection", Classes = { "Accent" } };
        var result = Hint(_results.GetValueOrDefault(id) ?? "");
        test.Click += async (_, _) => await _TestAsync(id, test, result).ConfigureAwait(true);
        (_test, _result) = (test, result);
        var remove = new Button { Content = "Remove connection", Classes = { "Ghost" } };
        remove.Click += (_, _) =>
        {
            _connections.RemoveAll(c => c.Id == id);
            _typed.Remove(id);
            _Render();
        };
        var actions = new DockPanel();
        DockPanel.SetDock(remove, Dock.Right);
        actions.Children.Add(remove);
        actions.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { test, result } });
        editor.Children.Add(actions);
        return editor;
    }

    private Control _TrustBlock(RemoteConnection connection)
    {
        if (connection.Kind == RemoteFilesChannel.Ftp || (connection.Kind == RemoteFilesChannel.WebDav && connection.DavUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
        {
            return Hint("No encryption, so there is no host key or certificate to check.");
        }
        if (connection.Pin is { } pin)
        {
            var staged = _staged.Contains(connection.Id);
            var block = new StackPanel { Spacing = 4, Children = { Mono(pin.Fingerprint) } };
            block.Children.Add(Hint(staged ? $"Will trust {pin.Algorithm}. Saved with Apply." : $"Trusted {pin.Algorithm} · {pin.Trusted.LocalDateTime:yyyy-MM-dd}"));
            var forget = new Button { Content = staged ? "Don't trust" : "Forget trusted key…", Classes = { "Compact" }, HorizontalAlignment = HorizontalAlignment.Left };
            forget.Click += async (_, _) =>
            {
                if (staged)
                {
                    _Forget(connection.Id);
                    return;
                }
                await _showDialog("Forget the trusted host key?", () => RemoteDialogs.Forget(connection, () => _Forget(connection.Id))).ConfigureAwait(true);
            };
            block.Children.Add(forget);
            return block;
        }
        return Hint(connection.Kind == RemoteFilesChannel.Sftp
            ? "Not verified yet. Test connection reads the host key first and shows it to you. Nothing is sent before you trust it."
            : $"Checked by this {(_remote ? "server" : "computer")}'s certificate store. A self-signed certificate asks for your confirmation on Test connection, the same way as a host key.");
    }

    private void _Forget(string id)
    {
        _staged.Remove(id);
        _results.Remove(id);
        _Update(id, c => c with { Pin = null }, render: true);
    }

    private Control _Secret(string id, string label, bool isSet, string placeholder, Func<RemoteSecrets, string?, RemoteSecrets> apply)
    {
        var box = new TextBox
        {
            PasswordChar = '•',
            PlaceholderText = isSet ? $"Leave blank to keep the stored {label.ToLowerInvariant()}" : placeholder,
            MinWidth = 260,
        };
        box.TextChanged += (_, _) => _typed[id] = apply(_typed.GetValueOrDefault(id) ?? new RemoteSecrets(), box.Text is { Length: > 0 } text ? text : null);
        return _Fields((label, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { box, _SetPill(isSet) } }));
    }

    private async Task _LoadKeyAsync(string id)
    {
        if (TopLevel.GetTopLevel(this) is not { } top
            || await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select private key" }).ConfigureAwait(true) is not [var file, ..])
        {
            return;
        }
        try
        {
            await using var stream = await file.OpenReadAsync().ConfigureAwait(true);
            using var reader = new StreamReader(stream);
            var buffer = new char[64 * 1024];
            var length = await reader.ReadBlockAsync(buffer).ConfigureAwait(true);
            if (length == buffer.Length || length == 0)
            {
                _results[id] = "This file is not a private key.";
            }
            else
            {
                _typed[id] = (_typed.GetValueOrDefault(id) ?? new RemoteSecrets()) with { Key = new string(buffer, 0, length) };
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _results[id] = "The key file could not be read.";
        }
        _Render();
    }

    private async Task _TestAsync(string id, Button test, TextBlock result)
    {
        test.IsEnabled = false;
        result.Text = "Testing…";
        var connection = _Get(id);
        RemoteTestAnswer? answer;
        try
        {
            answer = await _InvokeAsync<RemoteTestAnswer>(RemoteFilesChannel.Test, new RemoteTestRequest(connection, _typed.GetValueOrDefault(id))).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            answer = new RemoteTestAnswer(RemoteTestStatus.Failed, null, "The plugin could not be asked. Check the connection and try again.");
        }
        test.IsEnabled = true;
        switch (answer)
        {
            case { Status: RemoteTestStatus.Untrusted, Presented: { } presented }:
                result.Text = "";
                await _showDialog(presented.Algorithm == "certificate" ? "Verify the server certificate" : "Verify the host key",
                    () => RemoteDialogs.Trust(connection, presented, () => _Trust(id, presented))).ConfigureAwait(true);
                break;
            case { Status: RemoteTestStatus.Changed, Presented: { } presented }:
                result.Text = "Refused: the key changed.";
                await _showDialog("Host key changed", () => RemoteDialogs.Changed(connection, presented)).ConfigureAwait(true);
                break;
            case { Status: RemoteTestStatus.Connected }:
                _results[id] = "Connected and signed in.";
                result.Text = _results[id];
                break;
            default:
                _results[id] = answer?.Message ?? "The plugin gave no answer. Try again.";
                result.Text = _results[id];
                break;
        }
    }

    // Staged like every other setting, but the test that follows already uses it.
    private void _Trust(string id, RemotePresented presented)
    {
        _staged.Add(id);
        _open = id;
        _Update(id, c => c with { Pin = new RemotePin(presented.Algorithm, presented.Fingerprint, DateTimeOffset.UtcNow) }, render: true);
        if (_test is { } test && _result is { } result)
        {
            _ = _TestAsync(id, test, result);
        }
    }

    private async Task<T?> _InvokeAsync<T>(string action, object request) =>
        (await _channel.InvokeAsync(action, JsonSerializer.SerializeToElement(request, RemoteFilesChannel.Json)).ConfigureAwait(true))
            .Deserialize<T>(RemoteFilesChannel.Json);

    private static Control _Fields(params (string Label, Control Field)[] fields)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        foreach (var (label, field) in fields)
        {
            row.Children.Add(new StackPanel { Spacing = 4, Children = { Hint(label), field } });
            if (field is TextBox box)
            {
                box.MinWidth = Math.Max(box.MinWidth, 220);
            }
        }
        return row;
    }

    private static Border _Banner()
    {
        var icon = _Icon(MaterialIconKind.InformationOutline);
        icon.Bind(MaterialIcon.ForegroundProperty, icon.GetResourceObservable("CockpitAccentBrush"));
        var text = new StackPanel
        {
            Margin = new Thickness(10, 0, 0, 0),
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = "Keys and passwords you enter here are write-only.", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                Hint("The server stores them and only tells you whether one is set. Connections run from the server: it contacts the host you enter, not this computer."),
            },
        };
        var banner = new Border { IsVisible = false, Padding = new Thickness(12, 10), Margin = new Thickness(0, 0, 0, 6), Child = new DockPanel { Children = { icon, text } } };
        banner.Bind(Border.BackgroundProperty, banner.GetResourceObservable("CockpitCategoryTintBlueBrush"));
        banner.Bind(Border.CornerRadiusProperty, banner.GetResourceObservable("CockpitControlRadius"));
        return banner;
    }

    internal static Border Refusal(string title, string reason)
    {
        var icon = _Icon(MaterialIconKind.AlertCircleOutline);
        icon.Classes.Add("errorIcon");
        icon.VerticalAlignment = VerticalAlignment.Top;
        var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0), Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, Hint(reason) } };
        return new Border
        {
            Classes = { "errorCard", "blocking" },
            Padding = new Thickness(12, 10),
            Child = new DockPanel { Children = { icon, text } },
        };
    }

    private static Border _SetPill(bool isSet) => isSet ? _Pill("Set", "CockpitStatusDoneBrush") : _Pill("Not set");

    private static Border _Pill(string text, string? brush = null)
    {
        var label = new TextBlock { Text = text, FontSize = 11 };
        var pill = new Border { Padding = new Thickness(8, 0), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center, Child = label };
        pill.Bind(Border.BorderBrushProperty, pill.GetResourceObservable(brush ?? "CockpitHairlineBrush"));
        if (brush is not null)
        {
            label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable(brush));
        }
        pill.Bind(Border.CornerRadiusProperty, pill.GetResourceObservable("CockpitPillRadius"));
        return pill;
    }

    private static MaterialIcon _Icon(MaterialIconKind kind) => new() { Kind = kind, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };

    internal static TextBlock Hint(string text) => new() { Text = text, Classes = { "optionsHint" }, TextWrapping = TextWrapping.Wrap };

    internal static SelectableTextBlock Mono(string text)
    {
        var mono = new SelectableTextBlock { Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        mono.Bind(TextBlock.FontFamilyProperty, mono.GetResourceObservable("CockpitMonoFont"));
        mono.Bind(TextBlock.ForegroundProperty, mono.GetResourceObservable("CockpitTextFaintBrush"));
        return mono;
    }
}
