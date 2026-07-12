using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.PromptLibrary;

/// <summary>
/// The "Prompt Library" dialog (#2): a searchable list of saved templates on the left, an editor on the right
/// (name + body + a field per <c>{{variable}}</c> found in the body), and Insert/Copy actions. Selecting a
/// template loads it into the editor; Save persists edits, New adds a blank one, Delete removes the selected
/// one — all via <see cref="PromptLibrarySettings"/>. Insert substitutes the variable fields into the body and
/// hands the result to <see cref="ICockpitActions.InjectIntoActiveSessionAsync"/>, falling back to the
/// clipboard when no session is active. Built in code, matching the other plugin dialogs.
/// </summary>
internal sealed class PromptLibraryDialogControl : UserControl
{
    private readonly PromptLibrarySettings _settings;
    private readonly ICockpitActions _actions;

    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly TextBox _nameBox;
    private readonly TextBox _bodyBox;
    private readonly StackPanel _variablesPanel;
    private readonly TextBlock _status;
    private readonly Button _insert;

    private readonly ObservableCollection<PromptTemplate> _visible = [];
    private readonly Dictionary<string, TextBox> _variableBoxes = new(StringComparer.Ordinal);

    private List<PromptTemplate> _all = [];
    private string? _selectedId;
    private bool _suppressSelection;
    private bool _loadingEditor;
    private IReadOnlyList<string> _renderedVariableNames = [];

    public PromptLibraryDialogControl(PromptLibrarySettings settings, ICockpitActions actions)
    {
        _settings = settings;
        _actions = actions;

        // Left column: search + list + new/delete.
        _search = new TextBox { PlaceholderText = "Search templates…", Margin = new Thickness(0, 0, 0, 6) };
        _search.TextChanged += (_, _) => _ApplyFilter();

        _list = new ListBox
        {
            ItemsSource = _visible,
            ItemTemplate = new FuncDataTemplate<PromptTemplate>((template, _) =>
                new TextBlock { Text = template?.Name, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2) }, true),
        };
        _list.SelectionChanged += (_, _) => _LoadSelected();

        var newButton = new Button { Content = "＋ New", HorizontalAlignment = HorizontalAlignment.Stretch };
        newButton.Click += (_, _) => _NewTemplate();
        var deleteButton = new Button { Content = "Delete", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(6, 0, 0, 0) };
        deleteButton.Click += (_, _) => _DeleteSelected();
        var listButtons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(0, 6, 0, 0) };
        Grid.SetColumn(newButton, 0);
        Grid.SetColumn(deleteButton, 1);
        listButtons.Children.Add(newButton);
        listButtons.Children.Add(deleteButton);

        var left = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(_search, 0);
        Grid.SetRow(_list, 1);
        Grid.SetRow(listButtons, 2);
        left.Children.Add(_search);
        left.Children.Add(_list);
        left.Children.Add(listButtons);

        // Right column: name + save, body, variables, actions.
        _nameBox = new TextBox { PlaceholderText = "Template name" };
        var saveButton = new Button { Content = "Save", Margin = new Thickness(8, 0, 0, 0) };
        saveButton.Click += (_, _) => _SaveEditor();
        var nameRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(saveButton, Dock.Right);
        nameRow.Children.Add(saveButton);
        nameRow.Children.Add(_nameBox);

        _bodyBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            PlaceholderText = "Prompt text. Use {{variable}} for fields to fill in when inserting.",
        };
        _bodyBox.TextChanged += (_, _) => _RebuildVariablesIfChanged();

        _variablesPanel = new StackPanel { Spacing = 4 };
        var variablesScroll = new ScrollViewer { MaxHeight = 150, Margin = new Thickness(0, 6, 0, 0), Content = _variablesPanel };

        _insert = new Button { Content = "Insert into session", Classes = { "Accent" } };
        _insert.Click += async (_, _) => await _InsertAsync();
        var copyButton = new Button { Content = "⧉ Copy", Margin = new Thickness(6, 0, 0, 0) };
        copyButton.Click += async (_, _) => await _CopyAsync();
        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        actionRow.Children.Add(_insert);
        actionRow.Children.Add(copyButton);
        actionRow.Children.Add(_status);

        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetRow(nameRow, 0);
        Grid.SetRow(_bodyBox, 1);
        Grid.SetRow(variablesScroll, 2);
        Grid.SetRow(actionRow, 3);
        right.Children.Add(nameRow);
        right.Children.Add(_bodyBox);
        right.Children.Add(variablesScroll);
        right.Children.Add(actionRow);

        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*"), Margin = new Thickness(16) };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        split.Children.Add(left);
        split.Children.Add(right);
        Content = split;

        _Reload();
    }

    private void _Reload()
    {
        _all = [.. _settings.Load()];
        _ApplyFilter();
    }

    private void _ApplyFilter()
    {
        var query = _search.Text?.Trim();
        var previouslySelected = _selectedId;

        _suppressSelection = true;
        _visible.Clear();
        foreach (var template in _all)
        {
            if (string.IsNullOrEmpty(query)
                || template.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || template.Body.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                _visible.Add(template);
            }
        }

        _suppressSelection = false;

        var restore = _visible.FirstOrDefault(t => t.Id == previouslySelected);
        if (restore is not null)
        {
            _list.SelectedItem = restore;
        }
        else if (_visible.Count > 0)
        {
            _list.SelectedIndex = 0;
        }
        else
        {
            _selectedId = null;
            _LoadEditor(null);
        }
    }

    private void _LoadSelected()
    {
        if (_suppressSelection)
        {
            return;
        }

        var selected = _list.SelectedItem as PromptTemplate;
        _selectedId = selected?.Id;
        _LoadEditor(selected);
    }

    private void _LoadEditor(PromptTemplate? template)
    {
        _loadingEditor = true;
        _nameBox.Text = template?.Name ?? string.Empty;
        _bodyBox.Text = template?.Body ?? string.Empty;
        _loadingEditor = false;

        _RenderVariables(PromptVariables.Extract(_bodyBox.Text), preserveValues: false);

        var hasTemplate = template is not null;
        _nameBox.IsEnabled = hasTemplate;
        _bodyBox.IsEnabled = hasTemplate;
        _insert.IsEnabled = hasTemplate;
        _status.Text = string.Empty;
    }

    private void _RebuildVariablesIfChanged()
    {
        if (_loadingEditor)
        {
            return;
        }

        var names = PromptVariables.Extract(_bodyBox.Text);
        if (names.SequenceEqual(_renderedVariableNames))
        {
            return;
        }

        _RenderVariables(names, preserveValues: true);
    }

    private void _RenderVariables(IReadOnlyList<string> names, bool preserveValues)
    {
        var previousValues = preserveValues
            ? _variableBoxes.ToDictionary(kv => kv.Key, kv => kv.Value.Text ?? string.Empty)
            : [];

        _variablesPanel.Children.Clear();
        _variableBoxes.Clear();

        if (names.Count == 0)
        {
            _variablesPanel.Children.Add(new TextBlock
            {
                Text = "No variables. Add {{name}} in the body to create fill-in fields.",
                Opacity = 0.6,
                FontSize = 11,
            });
        }
        else
        {
            _variablesPanel.Children.Add(new TextBlock { Text = "Variables", FontWeight = FontWeight.SemiBold, FontSize = 11, Opacity = 0.7 });
            foreach (var name in names)
            {
                var box = new TextBox
                {
                    PlaceholderText = name,
                    Text = previousValues.TryGetValue(name, out var value) ? value : string.Empty,
                };
                var label = new TextBlock { Text = name, Width = 110, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
                DockPanel.SetDock(label, Dock.Left);
                row.Children.Add(label);
                row.Children.Add(box);
                _variablesPanel.Children.Add(row);
                _variableBoxes[name] = box;
            }
        }

        _renderedVariableNames = names;
    }

    private void _SaveEditor()
    {
        if (_selectedId is null)
        {
            return;
        }

        var name = _nameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            _status.Text = "Give the template a name first.";
            return;
        }

        var updated = new PromptTemplate(_selectedId, name, _bodyBox.Text ?? string.Empty);
        var index = _all.FindIndex(t => t.Id == _selectedId);
        if (index >= 0)
        {
            _all[index] = updated;
        }
        else
        {
            _all.Add(updated);
        }

        _settings.Save(_all);
        _ApplyFilter();
        _status.Text = "Saved.";
    }

    private void _NewTemplate()
    {
        var template = new PromptTemplate(PromptLibrarySettings.NewId(), "New template", string.Empty);
        _all.Add(template);
        _settings.Save(_all);
        _selectedId = template.Id;
        _search.Text = string.Empty;
        _ApplyFilter();
        _nameBox.Focus();
        _nameBox.SelectAll();
        _status.Text = "New template — edit and Save.";
    }

    private void _DeleteSelected()
    {
        if (_selectedId is null)
        {
            return;
        }

        _all.RemoveAll(t => t.Id == _selectedId);
        _settings.Save(_all);
        _selectedId = null;
        _ApplyFilter();
        _status.Text = "Deleted.";
    }

    private async Task _InsertAsync()
    {
        var text = _RenderCurrent();
        if (string.IsNullOrWhiteSpace(text))
        {
            _status.Text = "Nothing to insert.";
            return;
        }

        if (_actions.HasActiveSession)
        {
            await _actions.InjectIntoActiveSessionAsync(text);
            _status.Text = "Inserted into the active session.";
        }
        else
        {
            await _actions.SetClipboardTextAsync(text);
            _status.Text = "No active session — copied to the clipboard instead.";
        }
    }

    private async Task _CopyAsync()
    {
        var text = _RenderCurrent();
        if (string.IsNullOrWhiteSpace(text))
        {
            _status.Text = "Nothing to copy.";
            return;
        }

        await _actions.SetClipboardTextAsync(text);
        _status.Text = "Copied to the clipboard.";
    }

    private string _RenderCurrent()
    {
        var values = _variableBoxes.ToDictionary(kv => kv.Key, kv => kv.Value.Text ?? string.Empty);
        return PromptVariables.Substitute(_bodyBox.Text, values);
    }
}
