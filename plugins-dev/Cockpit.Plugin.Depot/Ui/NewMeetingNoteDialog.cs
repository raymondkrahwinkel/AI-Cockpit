using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Depot.Contracts;

namespace Cockpit.Plugin.Depot.UI;

// AC-1579: Depot's "New meeting note" dialog, natively. It only gathers choices; Depot makes the Markdown from its template.
internal sealed class NewMeetingNoteDialog : UserControl
{
    private readonly IDepotMeetingNotes _notes;
    private readonly Func<string, string, Task> _open;
    private readonly DateOnly _today;
    private readonly TimeSpan _checkDelay;
    private readonly ComboBox _project = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _topic = new() { Name = "MeetingNoteTopic", MaxLength = 200 };
    private readonly ComboBox _folder = new() { MinWidth = 120 };
    private readonly TextBlock _name = new() { FontFamily = _Mono(), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _prefix = new() { FontFamily = _Mono(), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _projectHint = new() { Text = "Only projects where you are Editor or higher.", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _attendeeHint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _full = new() { FontFamily = _Mono(), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _exists = new() { IsVisible = false, Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1) };
    private readonly TextBlock _existsText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly RadioButton _meeting = _Template("Meeting note", "Date, attendees, notes, action items", DepotMeetingNoteTemplate.Meeting, true);
    private readonly RadioButton _empty = _Template("Empty", "Only the title", DepotMeetingNoteTemplate.Empty, false);
    private readonly WrapPanel _chips = new() { Orientation = Orientation.Horizontal };
    private readonly ComboBox _addPerson = new() { PlaceholderText = "Add a colleague…", MinWidth = 180 };
    private readonly CheckBox _openInCockpit = new() { Name = "OpenInCockpit", Content = "Open in Cockpit", IsChecked = true };
    private readonly Button _create = new() { Name = "CreateMeetingNote", Classes = { "Accent" }, Content = "Create and open", IsEnabled = false };
    private readonly List<DepotMeetingNotePerson> _people = [];
    private readonly List<DepotMeetingNotePerson> _attendees = [];
    private CancellationTokenSource? _checking;
    private DepotMeetingNoteProject? _current;
    private Task _selecting = Task.CompletedTask;
    private Task _check = Task.CompletedTask;
    private bool _taken;

    public NewMeetingNoteDialog(IDepotMeetingNotes notes, Func<string, string, Task> open, DateOnly today, TimeSpan? checkDelay = null)
    {
        _checkDelay = checkDelay ?? TimeSpan.FromMilliseconds(300);
        _notes = notes;
        _open = open;
        _today = today;
        _project.ItemTemplate = new FuncDataTemplate<DepotMeetingNoteProject>((project, _) => _ProjectRow(project));
        _addPerson.ItemTemplate = new FuncDataTemplate<DepotMeetingNotePerson>((person, _) => _PersonRow(person));
        _folder.ItemTemplate = new FuncDataTemplate<string>((path, _) => new TextBlock { Text = string.IsNullOrEmpty(path) ? "/" : path + "/", FontFamily = _Mono() });
        _project.SelectionChanged += (_, _) => _selecting = _SelectProjectAsync();
        _topic.TextChanged += (_, _) => _Changed();
        _folder.SelectionChanged += (_, _) => _Changed();
        _addPerson.SelectionChanged += (_, _) => _AddPerson();
        _openInCockpit.IsCheckedChanged += (_, _) => _create.Content = _openInCockpit.IsChecked == true ? "Create and open" : "Create";
        _create.Click += async (_, _) => await _CreateAsync().ConfigureAwait(true);
        var cancel = new Button { Content = "Cancel", Classes = { "Ghost" } };
        cancel.Click += (_, _) => _Close();
        _error.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitStatusErrorBrush"));
        _projectHint.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextSecondaryBrush"));
        _hint.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextSecondaryBrush"));
        _attendeeHint.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextSecondaryBrush"));
        _prefix.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextSecondaryBrush"));
        _full.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextSecondaryBrush"));
        _exists.Bind(Border.BackgroundProperty, this.GetResourceObservable("CockpitStatusWaitingChipTintBrush"));
        _exists.Bind(Border.BorderBrushProperty, this.GetResourceObservable("CockpitStatusWaitingRingTintBrush"));
        _hint.Text = "Today's date + topic. Folder per project (default Meetings/).";
        _chips.Children.Add(_Chip("You", null));
        var path = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 0) };
        path.Children.Add(_prefix);
        Grid.SetColumn(_folder, 1);
        path.Children.Add(_folder);
        Grid.SetColumn(_name, 2);
        path.Children.Add(_name);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        footer.Children.Add(_full);
        Grid.SetColumn(cancel, 1);
        footer.Children.Add(cancel);
        Grid.SetColumn(_create, 2);
        footer.Children.Add(_create);
        _exists.Child = _existsText;
        Content = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = "Depot project" }, _project,
                _projectHint,
                new TextBlock { Text = "Topic" }, _topic,
                new TextBlock { Text = "File" }, path, _hint, _exists,
                new TextBlock { Text = "Template" },
                new UniformGrid { Columns = 2, Children = { _meeting, _empty } },
                new TextBlock { Text = "Attendees" },
                new StackPanel { Spacing = 4, Children = { _chips, _addPerson } },
                _attendeeHint, _openInCockpit, _error, footer,
            },
        };
        Loading = _LoadAsync();
    }

    // Finished when the projects are in and the first one is chosen with its folders and people; never faults.
    public Task Loading { get; }

    // For the scenes and tests: a topic and attendees as if typed and picked.
    internal void Prefill(string topic, params string[] attendeeIds)
    {
        _attendees.AddRange(_people.Where(person => attendeeIds.Contains(person.Id)));
        _RebuildChips();
        _RefreshPeople();
        _topic.Text = topic;
    }

    private static FontFamily _Mono() =>
        Application.Current?.TryFindResource("CockpitMonoFont", out var value) == true && value is FontFamily family ? family : FontFamily.Default;

    private static RadioButton _Template(string title, string detail, string id, bool selected) => new()
    {
        GroupName = "MeetingNoteTemplate",
        Tag = id,
        IsChecked = selected,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Content = new StackPanel { Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = detail, FontSize = 12, Opacity = 0.7 } } },
    };

    private async Task _LoadAsync()
    {
        try
        {
            var answer = await _notes.OptionsAsync().ConfigureAwait(true);
            if (answer.Data is not { } options)
            {
                _error.Text = answer.Message ?? "Depot did not answer.";
                return;
            }

            if (options.Projects.Count == 0)
            {
                _error.Text = "You are not an Editor of any project yet, so there is nowhere to start a meeting note.";
                return;
            }

            _project.ItemsSource = options.Projects;
            _project.SelectedItem = options.Recent.Select(address => options.Projects.FirstOrDefault(project => project.Address == address && project.Allowed)).FirstOrDefault(project => project is not null)
                ?? options.Projects.FirstOrDefault(project => project.Allowed) ?? options.Projects[0];
            await _SelectProjectAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Depot: loading the meeting-note dialog failed: {exception}");
            _error.Text = "The meeting-note dialog could not be loaded. Try again.";
        }
    }

    // Never faults: the SelectionChanged handler cannot await it.
    private async Task _SelectProjectAsync()
    {
        try
        {
            await _SelectCoreAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Depot: choosing the meeting-note project failed: {exception}");
            _error.Text = "Could not load that project. Try again.";
        }
    }

    private async Task _SelectCoreAsync()
    {
        if (_project.SelectedItem is not DepotMeetingNoteProject project || project == _current)
        {
            return;
        }

        _current = project;
        _attendees.Clear();
        _RebuildChips();
        _attendeeHint.Text = $"Members of {project.Organization}. They get an invitation in Depot and, if they use Cockpit, a notice there.";
        var answer = await _notes.ProjectAsync(project.Address).ConfigureAwait(true);
        if (_current != project)
        {
            return;
        }

        var folder = string.IsNullOrWhiteSpace(project.Folder) ? DepotMeetingNoteName.DefaultFolder : project.Folder.Trim('/');
        _folder.ItemsSource = new[] { string.Empty }.Concat(answer.Data?.Folders ?? []).Append(folder).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        _folder.SelectedItem = folder;
        _people.Clear();
        _people.AddRange(answer.Data?.People ?? []);
        _RefreshPeople();
        _error.Text = answer.Data is null ? answer.Message : _PlanMessage(project);
        _Changed();
    }

    private static string? _PlanMessage(DepotMeetingNoteProject project) =>
        project.Allowed ? null : $"{project.Organization}'s plan has no notes-taker.";

    private string? _Folder => _folder.SelectedItem as string;

    private string _Topic => (_topic.Text ?? string.Empty).Trim();

    private string _Path => DepotMeetingNoteName.Build(_Folder, _Topic.Length == 0 ? "Topic" : _Topic, _today);

    private void _Changed()
    {
        if (_current is null)
        {
            return;
        }

        var path = _Path;
        _prefix.Text = _current.Address + "/";
        _name.Text = path[(path.LastIndexOf('/') + 1)..];
        _full.Text = _current.Name + "/" + path;
        _taken = false;
        _exists.IsVisible = false;
        _create.IsEnabled = false;
        _checking?.Cancel();
        _checking?.Dispose();
        _checking = new CancellationTokenSource();
        _check = _CheckAsync(_current, _Topic, _Folder, _checking.Token);
    }

    // A taken name is an answer, not an error: it says who made the note and how many are in it now.
    private async Task _CheckAsync(DepotMeetingNoteProject project, string topic, string? folder, CancellationToken cancellationToken)
    {
        try
        {
            if (topic.Length == 0 || !project.Allowed)
            {
                return;
            }

            await Task.Delay(_checkDelay, cancellationToken).ConfigureAwait(true);
            var answer = await _notes.CheckAsync(project.Address, folder, topic).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (answer.Data is { Exists: true } check)
            {
                _taken = true;
                _existsText.Text = "Already exists" + (check.By is null ? "." : $": {check.By} made it" + (check.At is { } at ? $" at {at.LocalDateTime:HH:mm}." : "."))
                    + (check.Live > 0 ? $" {check.Live} in it now." : string.Empty);
                _exists.IsVisible = true;
            }

            _create.IsEnabled = !_taken;
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke took over.
        }
        catch (Exception exception)
        {
            // Depot's create is create-only and answers a taken name itself, so a failed check does not block it.
            Trace.TraceWarning($"Depot: checking the meeting-note name failed: {exception}");
            _create.IsEnabled = !cancellationToken.IsCancellationRequested;
        }
    }

    private void _AddPerson()
    {
        if (_addPerson.SelectedItem is DepotMeetingNotePerson person)
        {
            _attendees.Add(person);
            _RebuildChips();
            _RefreshPeople();
        }
    }

    private void _RefreshPeople()
    {
        _addPerson.SelectedItem = null;
        _addPerson.ItemsSource = _people.Where(person => _attendees.All(chosen => chosen.Id != person.Id)).ToList();
    }

    private void _RebuildChips()
    {
        _chips.Children.Clear();
        _chips.Children.Add(_Chip("You", null));
        foreach (var person in _attendees)
        {
            _chips.Children.Add(_Chip(person.Name, () =>
            {
                _attendees.Remove(person);
                _RebuildChips();
                _RefreshPeople();
            }));
        }
    }

    private Border _Chip(string name, Action? remove)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center } } };
        if (remove is not null)
        {
            var button = new Button { Content = "×", Classes = { "Subtle" }, Padding = new Thickness(2, 0) };
            button.Click += (_, _) => remove();
            row.Children.Add(button);
        }

        var chip = new Border { CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1), Padding = new Thickness(10, 2), Margin = new Thickness(0, 0, 6, 4), Child = row };
        chip.Bind(Border.BorderBrushProperty, this.GetResourceObservable("CockpitHairlineBrush"));
        return chip;
    }

    private Control _ProjectRow(DepotMeetingNoteProject? project) => project is null ? new TextBlock() : _Row(
        new TextBlock { Text = project.Name, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
        new TextBlock { Text = project.Address, FontFamily = _Mono(), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center },
        project.Allowed ? project.Role : "Not in your plan");

    private Control _PersonRow(DepotMeetingNotePerson? person) => person is null ? new TextBlock() : _Row(
        new TextBlock { Text = person.Name, VerticalAlignment = VerticalAlignment.Center }, null, person.Role ?? "no access");

    private static Control _Row(Control first, Control? second, string badge)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { first } };
        if (second is not null)
        {
            row.Children.Add(second);
        }

        row.Children.Add(new TextBlock { Text = badge, FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private async Task _CreateAsync()
    {
        if (_current is not { Allowed: true } project || _Topic.Length == 0 || _taken)
        {
            return;
        }

        _create.IsEnabled = false;
        _error.Text = string.Empty;
        try
        {
            var template = (_empty.IsChecked == true ? _empty : _meeting).Tag as string ?? DepotMeetingNoteTemplate.Meeting;
            var answer = await _notes.CreateAsync(project.Address, _Topic, _Folder, template, [.. _attendees.Select(person => person.Id)]).ConfigureAwait(true);
            if (answer.Data is not { } created)
            {
                _error.Text = answer.Message ?? "Could not create the note.";
                _create.IsEnabled = true;
                return;
            }

            if (created.Existing)
            {
                _taken = true;
                _existsText.Text = "Already exists.";
                _exists.IsVisible = true;
                return;
            }

            if (_openInCockpit.IsChecked == true && await _TryOpenAsync(project, created.Path).ConfigureAwait(true))
            {
                _Close();
                return;
            }

            ShowCreated(project, created.Path, [.. _attendees.Select(person => person.Name)]);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Trace.TraceWarning($"Depot: creating the meeting note failed: {exception}");
            _error.Text = "Could not create the note. Try again.";
            _create.IsEnabled = true;
        }
    }

    private async Task<bool> _TryOpenAsync(DepotMeetingNoteProject project, string path)
    {
        try
        {
            await _open(project.Address, path).ConfigureAwait(true);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Trace.TraceWarning($"Depot: opening the new meeting note failed: {exception}");
            return false;
        }
    }

    // The note exists in Depot whatever happens next, so this says so and offers the open again.
    internal void ShowCreated(DepotMeetingNoteProject project, string path, IReadOnlyList<string> invited)
    {
        var folder = path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty;
        var where = $"{project.Name}" + (folder.Length > 0 ? $" › {folder}" : string.Empty);
        var text = new TextBlock
        {
            Text = $"✓ Created in {where}." + (invited.Count == 0 ? string.Empty : $" {_Names(invited)} got an invitation (Depot inbox and their Cockpit)."),
            TextWrapping = TextWrapping.Wrap,
        };
        var banner = new Border { Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Child = text };
        banner.Bind(Border.BackgroundProperty, this.GetResourceObservable("CockpitStatusDoneChipTintBrush"));
        banner.Bind(Border.BorderBrushProperty, this.GetResourceObservable("CockpitStatusDoneRingTintBrush"));
        var failed = new TextBlock { TextWrapping = TextWrapping.Wrap };
        failed.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitStatusErrorBrush"));
        var open = new Button { Content = "Open in Cockpit", Classes = { "Accent" } };
        open.Click += async (_, _) =>
        {
            failed.Text = await _TryOpenAsync(project, path).ConfigureAwait(true) ? string.Empty : "The note could not be opened. Try again.";
            if (failed.Text.Length == 0)
            {
                _Close();
            }
        };
        var close = new Button { Content = "Close", Classes = { "Ghost" } };
        close.Click += (_, _) => _Close();
        Content = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(16),
            Children =
            {
                banner, failed,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { close, open } },
            },
        };
    }

    private static string _Names(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    private void _Close() => (TopLevel.GetTopLevel(this) as Window)?.Close();
}
