using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Controls;

// AC-1600: one row of the Projects list. Selecting goes through the host's `SelectCommand`, so the selection lives in
// `ProjectsViewModel` and survives a rebuild of the rows; a double click starts the project when it can be started.
public partial class ProjectListRow : UserControl
{
    public static readonly StyledProperty<ICommand?> SelectCommandProperty =
        AvaloniaProperty.Register<ProjectListRow, ICommand?>(nameof(SelectCommand));

    public static readonly StyledProperty<bool> IsCompactProperty =
        AvaloniaProperty.Register<ProjectListRow, bool>(nameof(IsCompact));

    public static readonly DirectProperty<ProjectListRow, bool> ShowDescriptionUnderNameProperty =
        AvaloniaProperty.RegisterDirect<ProjectListRow, bool>(nameof(ShowDescriptionUnderName), row => row.ShowDescriptionUnderName);

    private ProjectListItemViewModel? _item;
    private bool _showDescriptionUnderName = true;

    static ProjectListRow()
    {
        IsCompactProperty.Changed.AddClassHandler<ProjectListRow>((row, _) => row._UpdateLayout());
        ProjectListGrid.IsWideProperty.Changed.AddClassHandler<ProjectListRow>((row, _) => row._UpdateLayout());
    }

    public ProjectListRow()
    {
        InitializeComponent();
        _UpdateLayout();
        Tapped += (_, _) => SelectCommand?.Execute(DataContext);
        // A double click on the row's own buttons is theirs: Start already ran on the click.
        DoubleTapped += (_, args) =>
        {
            if (args.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            {
                return;
            }

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

    public bool IsCompact
    {
        get => GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public bool ShowDescriptionUnderName => _showDescriptionUnderName;

    private void _UpdateLayout()
    {
        PseudoClasses.Set(":compact", IsCompact);
        SetAndRaise(ShowDescriptionUnderNameProperty, ref _showDescriptionUnderName, !IsCompact && !ProjectListGrid.GetIsWide(this));
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
