using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Cockpit.Plugin.Depot.UI;

internal sealed class NewMarkdownFileDialog : UserControl
{
    private readonly TextBox _name = new() { Name = "MarkdownFileName", PlaceholderText = "e.g. meeting-notes" };
    private readonly ComboBox _folder;
    private readonly TextBlock _path = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _create = new() { Content = "Create", Classes = { "Accent" }, IsEnabled = false };
    private readonly Func<string, bool> _exists;
    private readonly Func<string, Task> _open;

    public NewMarkdownFileDialog(string project, string folder, IReadOnlyList<string> folders,
        Func<string, bool> exists, Func<string, Task> open)
    {
        _exists = exists;
        _open = open;
        _folder = new ComboBox { ItemsSource = folders, SelectedItem = folder, HorizontalAlignment = HorizontalAlignment.Stretch };
        _folder.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((path, _) => new TextBlock { Text = string.IsNullOrEmpty(path) ? "/" : path });
        _name.TextChanged += (_, _) => _Validate();
        _folder.SelectionChanged += (_, _) => _Validate();
        _create.Click += async (_, _) => await _CreateAsync().ConfigureAwait(true);
        var cancel = new Button { Content = "Cancel", Classes = { "Ghost" } };
        cancel.Click += (_, _) => (TopLevel.GetTopLevel(this) as Window)?.Close();
        _error.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitStatusErrorBrush"));
        _path.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextSecondaryBrush"));
        Content = new StackPanel
        {
            Spacing = 8, Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = "Project" },
                new ComboBox { ItemsSource = new[] { project }, SelectedIndex = 0, IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch },
                new TextBlock { Text = "Folder" }, _folder,
                new TextBlock { Text = "Name" }, _name, _path, _error,
                new TextBlock { Text = "The file is created when you save it.", TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, _create } },
            },
        };
        AttachedToVisualTree += (_, _) => _name.Focus();
        _Validate();
    }

    private string _Target()
    {
        var name = (_name.Text ?? "").Trim();
        if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            name += ".md";
        }
        return _folder.SelectedItem is string { Length: > 0 } folder ? $"{folder}/{name}" : name;
    }

    private void _Validate()
    {
        var name = (_name.Text ?? "").Trim();
        var target = _Target();
        var stem = name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? name[..^3].Trim() : name;
        _path.Text = target;
        _error.Text = name.Length == 0 ? "" : stem is "" or "." or ".." || name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0']) >= 0
            ? "Choose a file name without path separators or special characters."
            : _exists(target) ? "This file already exists. Open it or choose another name." : "";
        _create.IsEnabled = name.Length > 0 && _error.Text.Length == 0;
    }

    private async Task _CreateAsync()
    {
        _Validate();
        if (!_create.IsEnabled)
        {
            return;
        }
        _create.IsEnabled = false;
        try
        {
            await _open(_Target()).ConfigureAwait(true);
            (TopLevel.GetTopLevel(this) as Window)?.Close();
        }
        catch (Exception) { _error.Text = "The file workspace could not be opened. Try again."; _create.IsEnabled = true; }
    }
}
