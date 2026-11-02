using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace Cockpit.Plugin.Depot.UI;

internal sealed class NewMarkdownFileDialog : UserControl
{
    private const string NewFolder = "<new folder>";
    private static readonly char[] _InvalidChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0'];
    private readonly TextBox _name = new() { Name = "MarkdownFileName", PlaceholderText = "e.g. meeting-notes" };
    private readonly TextBox _newFolderName = new() { Name = "NewFolderName", PlaceholderText = "e.g. Ideas" };
    private readonly ComboBox _folder;
    private readonly ComboBox _parent;
    private readonly StackPanel _newFolder;
    private readonly IReadOnlyList<string> _folders;
    private readonly TextBlock _path = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _folderError = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _create = new() { Content = "Create", Classes = { "Accent" }, IsEnabled = false };
    private readonly Func<string, bool> _exists;
    private readonly Func<string, Task> _open;
    private string _lastFolder;

    public NewMarkdownFileDialog(string project, string folder, IReadOnlyList<string> folders,
        Func<string, bool> exists, Func<string, Task> open)
    {
        _exists = exists;
        _open = open;
        _folders = folders;
        _lastFolder = folder;
        var template = new FuncDataTemplate<string>((path, _) =>
            new TextBlock { Text = path == NewFolder ? "New folder…" : string.IsNullOrEmpty(path) ? "/" : path });
        _folder = new ComboBox { ItemsSource = folders.Append(NewFolder).ToList(), SelectedItem = folder, ItemTemplate = template, HorizontalAlignment = HorizontalAlignment.Stretch };
        _parent = new ComboBox { ItemsSource = folders, SelectedItem = folder, ItemTemplate = template, HorizontalAlignment = HorizontalAlignment.Stretch };
        _newFolder = new StackPanel
        {
            Spacing = 8, IsVisible = false,
            Children = { new TextBlock { Text = "Inside" }, _parent, new TextBlock { Text = "Folder name" }, _newFolderName, _folderError },
        };
        _name.TextChanged += (_, _) => _Validate();
        _newFolderName.TextChanged += (_, _) => _Validate();
        _parent.SelectionChanged += (_, _) => _Validate();
        _folder.SelectionChanged += (_, _) => _FolderChanged();
        _create.Click += async (_, _) => await _CreateAsync().ConfigureAwait(true);
        var cancel = new Button { Content = "Cancel", Classes = { "Ghost" } };
        cancel.Click += (_, _) => (TopLevel.GetTopLevel(this) as Window)?.Close();
        _error.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitStatusErrorBrush"));
        _folderError.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitStatusErrorBrush"));
        _path.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextSecondaryBrush"));
        Content = new StackPanel
        {
            Spacing = 8, Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = "Project" },
                new ComboBox { ItemsSource = new[] { project }, SelectedIndex = 0, IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch },
                new TextBlock { Text = "Folder" }, _folder, _newFolder,
                new TextBlock { Text = "Name" }, _name, _path, _error,
                new TextBlock { Text = "The file is created when you save it.", TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, _create } },
            },
        };
        AttachedToVisualTree += (_, _) => _name.Focus();
        _Validate();
    }

    // Choosing "New folder…" keeps the previous folder as the parent; any other choice hides the name field.
    private void _FolderChanged()
    {
        if (_folder.SelectedItem as string == NewFolder)
        {
            _parent.SelectedItem = _lastFolder;
            _newFolder.IsVisible = true;
            _newFolderName.Focus();
        }
        else
        {
            _newFolder.IsVisible = false;
            _lastFolder = _folder.SelectedItem as string ?? "";
        }
        _Validate();
    }

    // For the screenshot scenes: the dialog as it looks right after "New folder…" was chosen and a name typed.
    internal void ChooseNewFolder(string folderName)
    {
        _folder.SelectedItem = NewFolder;
        _newFolderName.Text = folderName;
    }

    private bool _CreatingFolder => _newFolder.IsVisible;

    private string _ParentFolder => _parent.SelectedItem as string ?? "";

    private static string _Join(string folder, string name) => name.Length == 0 ? folder : folder.Length > 0 ? $"{folder}/{name}" : name;

    private string _Target()
    {
        var name = (_name.Text ?? "").Trim();
        if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            name += ".md";
        }
        var folder = _CreatingFolder ? _Join(_ParentFolder, (_newFolderName.Text ?? "").Trim()) : _folder.SelectedItem as string ?? "";
        return _Join(folder, name);
    }

    // Depot has no empty folders: the folder exists once the file is saved in it. "" means the name is fine.
    internal string FolderNameError(string name) =>
        name.Length == 0 ? "Enter a folder name."
        : name.Contains("..", StringComparison.Ordinal) || name.IndexOfAny(_InvalidChars) >= 0
            ? "Use a folder name without path separators, “..” or special characters."
        : _folders.Any(existing => string.Equals(existing, _Join(_ParentFolder, name), StringComparison.OrdinalIgnoreCase))
            ? "A folder with this name already exists here."
        : "";

    private void _Validate()
    {
        var name = (_name.Text ?? "").Trim();
        var folderName = (_newFolderName.Text ?? "").Trim();
        var folderError = _CreatingFolder ? FolderNameError(folderName) : "";
        _folderError.Text = folderName.Length == 0 ? "" : folderError;
        var target = _Target();
        var stem = name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? name[..^3].Trim() : name;
        _path.Text = target;
        _error.Text = name.Length == 0 ? "" : stem is "" or "." or ".." || name.IndexOfAny(_InvalidChars) >= 0
            ? "Choose a file name without path separators or special characters."
            : _exists(target) ? "This file already exists. Open it or choose another name." : "";
        _create.IsEnabled = name.Length > 0 && _error.Text.Length == 0 && folderError.Length == 0;
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
