using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using System.ComponentModel;
using System.Windows.Input;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// AC-543: plain UserControl over AssistantIndicatorViewModel, deliberately without a
// constructor arg or host reference — whatever embeds it (sidebar, AC-238 companion window)
// supplies its own view model instance, so this file never has to know which one it is in.
public partial class AssistantIndicator : UserControl
{
    public static readonly StyledProperty<bool> ShowWhenDisabledProperty =
        AvaloniaProperty.Register<AssistantIndicator, bool>(nameof(ShowWhenDisabled));

    public bool ShowWhenDisabled
    {
        get => GetValue(ShowWhenDisabledProperty);
        set => SetValue(ShowWhenDisabledProperty, value);
    }

    // Null follows the view model's IsCollapsed (the sidebar); a value pins this instance to the rail or the chip.
    public static readonly StyledProperty<bool?> CollapsedProperty =
        AvaloniaProperty.Register<AssistantIndicator, bool?>(nameof(Collapsed));

    public bool? Collapsed
    {
        get => GetValue(CollapsedProperty);
        set => SetValue(CollapsedProperty, value);
    }

    private AssistantIndicatorViewModel? _viewModel;

    internal ICommand? OpenCommand { get; init; }

    private void _OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (OpenCommand is { } open)
        {
            if (open.CanExecute(null))
            {
                open.Execute(null);
            }
        }
        else
        {
            _viewModel?.ClickCommand.Execute(null);
        }
    }

    public AssistantIndicator()
    {
        InitializeComponent();
        _SyncForm();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CollapsedProperty)
        {
            _SyncForm();
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= _OnViewModelChanged;
        }

        _viewModel = DataContext as AssistantIndicatorViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += _OnViewModelChanged;
        }

        _SyncForm();
    }

    private void _OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssistantIndicatorViewModel.IsCollapsed))
        {
            _SyncForm();
        }
    }

    private void _SyncForm()
    {
        var collapsed = Collapsed ?? _viewModel?.IsCollapsed ?? false;
        RailForm.IsVisible = collapsed;
        ChipForm.IsVisible = !collapsed;
    }
}
