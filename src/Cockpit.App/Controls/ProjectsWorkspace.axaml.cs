using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Controls;

public partial class ProjectsWorkspace : UserControl
{
    public static readonly StyledProperty<bool> IsNarrowProperty =
        AvaloniaProperty.Register<ProjectsWorkspace, bool>(nameof(IsNarrow));

    private ProjectsViewModel? _projects;
    private bool _showingDetail;

    public ProjectsWorkspace()
    {
        InitializeComponent();
        SelectProjectCommand = new RelayCommand<ProjectListItemViewModel>(_SelectProject);
        BackCommand = new RelayCommand(_ShowList);
        DataContextChanged += (_, _) => _projects = DataContext as ProjectsViewModel;
        SizeChanged += (_, _) => _ApplyLayout();
        LayoutUpdated += _OnInitialLayout;
        AddHandler(KeyDownEvent, _OnKeyDown, RoutingStrategies.Tunnel);
        _ApplyLayout();
    }

    public bool IsNarrow
    {
        get => GetValue(IsNarrowProperty);
        private set => SetValue(IsNarrowProperty, value);
    }

    public ICommand SelectProjectCommand { get; }

    public ICommand BackCommand { get; }

    private void _OnInitialLayout(object? sender, EventArgs args)
    {
        LayoutUpdated -= _OnInitialLayout;
        _ApplyLayout();
    }

    private void _SelectProject(ProjectListItemViewModel? item)
    {
        if (item is null || _projects is null)
        {
            return;
        }

        _projects.SelectListItemCommand.Execute(item);
        if (IsNarrow)
        {
            _showingDetail = true;
            _ApplyLayout();
        }
    }

    private void _ShowList()
    {
        _showingDetail = false;
        _ApplyLayout();
    }

    public void ShowDetail()
    {
        _showingDetail = true;
        _ApplyLayout();
    }

    private void _ApplyLayout()
    {
        var projectsSplit = this.FindControl<Grid>("ProjectsSplit");
        var projectList = this.FindControl<DockPanel>("ProjectList");
        var projectDetail = this.FindControl<ScrollViewer>("ProjectDetail");
        if (projectsSplit is null || projectList is null || projectDetail is null)
        {
            return;
        }

        IsNarrow = Bounds.Width < 720;
        // The columns stay the same objects: replacing them mid-layout left the grid arranged with the old ones.
        Grid.SetColumn(projectDetail, IsNarrow ? 0 : 1);
        projectList.IsVisible = !IsNarrow || !_showingDetail;
        projectDetail.IsVisible = !IsNarrow || _showingDetail;
        if (!IsNarrow)
        {
            _showingDetail = false;
        }

        projectsSplit.ColumnDefinitions[1].Width = new GridLength(IsNarrow ? 0 : Math.Clamp(Bounds.Width * 0.28, 360, 500));
    }

    private void _OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (_projects is null || !_IsListNavigationSource(args.Source))
        {
            return;
        }

        var rows = _projects.ProjectSections.Where(section => !section.IsCollapsed).SelectMany(section => section.Items).ToList();
        var index = rows.IndexOf(_projects.SelectedListItem!);
        if (args.Key is Key.Up or Key.Down && rows.Count > 0)
        {
            _projects.SelectListItemCommand.Execute(rows[Math.Clamp(index + (args.Key == Key.Up ? -1 : 1), 0, rows.Count - 1)]);
            args.Handled = true;
        }
        else if (args.Key == Key.Enter && _projects.SelectedListItem is { CanStart: true } item)
        {
            item.Actions?.Start.Execute(item.Project);
            args.Handled = true;
        }
    }

    private bool _IsListNavigationSource(object? source)
    {
        var projectList = this.FindControl<DockPanel>("ProjectList");
        if (source is not Visual visual || projectList is null)
        {
            return false;
        }

        foreach (var ancestor in visual.GetVisualAncestors().Prepend(visual))
        {
            if (ancestor == projectList)
            {
                return true;
            }

            if (ancestor is Button or ComboBox or MenuItem or TextBox
                || ancestor is Control { IsTabStop: true } and not ProjectListRow)
            {
                return false;
            }
        }

        return false;
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}
