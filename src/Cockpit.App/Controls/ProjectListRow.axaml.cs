using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Controls;

// AC-1600: one row of the Projects list. Selecting goes through the host's `SelectCommand`, so the selection lives in
// `ProjectsViewModel` and survives a rebuild of the rows; a double click starts the project when it can be started.
public partial class ProjectListRow : UserControl
{
    public static readonly StyledProperty<ICommand?> SelectCommandProperty =
        AvaloniaProperty.Register<ProjectListRow, ICommand?>(nameof(SelectCommand));

    private ProjectListItemViewModel? _item;

    public ProjectListRow()
    {
        InitializeComponent();
        Tapped += (_, _) => SelectCommand?.Execute(DataContext);
        DoubleTapped += (_, _) =>
        {
            if (DataContext is ProjectListItemViewModel { CanStart: true } item)
            {
                item.Actions?.Start.Execute(item.Project);
            }
        };
    }

    public ICommand? SelectCommand
    {
        get => GetValue(SelectCommandProperty);
        set => SetValue(SelectCommandProperty, value);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_item is not null)
        {
            _item.PropertyChanged -= _OnItemChanged;
        }

        _item = DataContext as ProjectListItemViewModel;
        if (_item is not null)
        {
            _item.PropertyChanged += _OnItemChanged;
        }

        PseudoClasses.Set(":selected", _item?.IsSelected == true);
    }

    private void _OnItemChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ProjectListItemViewModel.IsSelected))
        {
            PseudoClasses.Set(":selected", _item?.IsSelected == true);
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
